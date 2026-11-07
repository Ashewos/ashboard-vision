using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Trofeo.Extensions.Protocol;
using Trofeo.ModuleKit;
using Trofeo.Shared;

namespace Trofeo.Extensions.Service;

/// <summary>
/// Le relais « Cet ordinateur » d'un hôte de module : Windows interdit à un AppContainer de joindre un serveur du même PC, donc
/// c'est le service (qui n'est pas enfermé) qui se connecte à <c>127.0.0.1</c> et fait passer les octets, par deux tubes
/// anonymes dont les bouts sont dupliqués dans l'hôte. Il n'ouvre que les ports que l'utilisateur a saisis dans les champs
/// « port » (<see cref="PortField"/>) des réglages de la case qui les demande, seulement si la permission « Cet ordinateur »
/// est accordée, et au plus <see cref="MaxConnections"/> connexions à la fois. Un relais vit autant que l'hôte qu'il sert.
/// </summary>
public sealed class LocalRelay : IDisposable
{
    /// <summary>Le nombre maximal de connexions ouvertes en même temps pour un module.</summary>
    public const int MaxConnections = 16;

    /// <summary>La raison d'un refus de port (ou de permission).</summary>
    public const string PortRefusedReason = "Port non autorisé";

    /// <summary>La raison du refus d'une dix-septième connexion.</summary>
    public const string TooManyReason = "Trop de connexions ouvertes";

    /// <summary>La raison quand rien n'écoute sur ce port.</summary>
    public const string UnreachableReason = "Connexion impossible";

    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(5);

    private readonly IReadOnlyList<PortField> _portFields;
    private readonly bool _permitted;
    private readonly Func<SafeHandle, long> _duplicateIntoHost;
    private readonly Action<HostMessage> _send;
    private readonly TimeSpan _connectTimeout;
    private readonly CancellationTokenSource _closing = new();
    private readonly object _gate = new();
    private readonly List<Connection> _connections = [];
    private int _open;
    private bool _disposed;

    /// <param name="portFields">Les champs « port » du schéma des réglages du module.</param>
    /// <param name="permitted">Vrai si la permission « Cet ordinateur » est accordée.</param>
    /// <param name="duplicateIntoHost">Duplique une poignée dans l'hôte et renvoie sa valeur là-bas.</param>
    /// <param name="send">Envoie la réponse (<see cref="LocalConnected"/> ou <see cref="LocalRefused"/>) à l'hôte ; ne doit jamais bloquer.</param>
    /// <param name="connectTimeout">Le délai de connexion (5 s par défaut).</param>
    public LocalRelay(
        IReadOnlyList<PortField> portFields, bool permitted, Func<SafeHandle, long> duplicateIntoHost, Action<HostMessage> send,
        TimeSpan? connectTimeout = null)
    {
        _portFields = portFields ?? throw new ArgumentNullException(nameof(portFields));
        _permitted = permitted;
        _duplicateIntoHost = duplicateIntoHost ?? throw new ArgumentNullException(nameof(duplicateIntoHost));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _connectTimeout = connectTimeout ?? DefaultConnectTimeout;
    }

    /// <summary>Le nombre de connexions ouvertes en ce moment (une connexion compte jusqu'à la fin de ses deux sens).</summary>
    public int OpenConnections => Volatile.Read(ref _open);

    /// <summary>
    /// Vrai si <paramref name="port"/> est la valeur d'un champ « port » de ces réglages (ou, quand le réglage n'est pas écrit,
    /// la valeur proposée par le champ). Un réglage qui n'est pas un nombre entier ne compte pas. La règle vit dans <c>src/Shared/PortRule.cs</c>, partagée avec les aides de test des créateurs.
    /// </summary>
    public static bool IsAllowedPort(IReadOnlyList<PortField> portFields, JsonElement settings, int port)
        => PortRule.IsAllowed(portFields, settings, port);

    /// <summary>
    /// Traite une demande de connexion. <paramref name="settings"/> sont les réglages de l'instance qui la fait (ou, pendant une
    /// action de la fenêtre de réglage, ceux que cette action a reçus). Répond toujours : <see cref="LocalRefused"/>, ou
    /// <see cref="LocalConnected"/> une fois connecté. Ne bloque pas.
    /// </summary>
    public void Connect(LocalConnect request, JsonElement settings)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_permitted || !IsAllowedPort(_portFields, settings, request.Port))
        {
            _send(new LocalRefused(request.Id, PortRefusedReason));
            return;
        }

        if (Interlocked.Increment(ref _open) > MaxConnections)
        {
            Interlocked.Decrement(ref _open);
            _send(new LocalRefused(request.Id, TooManyReason));
            return;
        }

        _ = Task.Run(() => ConnectAsync(request));
    }

    /// <summary>Ferme toutes les connexions (l'hôte est arrêté).</summary>
    public void Dispose()
    {
        Connection[] connections;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            connections = [.. _connections];
            _connections.Clear();
        }

        _closing.Cancel();
        foreach (var connection in connections)
        {
            connection.Close();
        }
    }

    private async Task ConnectAsync(LocalConnect request)
    {
        TcpClient? tcp = null;
        AnonymousPipeServerStream? toModule = null;
        AnonymousPipeServerStream? fromModule = null;
        long readHandle;
        long writeHandle;
        try
        {
            tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token))
            {
                timeout.CancelAfter(_connectTimeout);
                await tcp.ConnectAsync(IPAddress.Loopback, request.Port, timeout.Token).ConfigureAwait(false);
            }

            // Deux tubes : le module lit l'un (bout « client » dupliqué chez lui) et écrit dans l'autre. Les copies du service
            // des bouts « client » sont fermées dès qu'ils sont dupliqués : la fin du module (ou de l'hôte) ferme alors vraiment le tube.
            toModule = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
            fromModule = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
            readHandle = _duplicateIntoHost(toModule.ClientSafePipeHandle);
            writeHandle = _duplicateIntoHost(fromModule.ClientSafePipeHandle);
            toModule.DisposeLocalCopyOfClientHandle();
            fromModule.DisposeLocalCopyOfClientHandle();
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException or ObjectDisposedException
                                       or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            tcp?.Dispose();
            toModule?.Dispose();
            fromModule?.Dispose();
            Interlocked.Decrement(ref _open);
            _send(new LocalRefused(request.Id, UnreachableReason));
            return;
        }

        var connection = new Connection(this, tcp, toModule, fromModule);
        bool registered;
        lock (_gate)
        {
            registered = !_disposed;
            if (registered)
            {
                _connections.Add(connection);
            }
        }

        if (!registered)
        {
            connection.Close();
            Interlocked.Decrement(ref _open);
            return;
        }

        _send(new LocalConnected(request.Id, readHandle, writeHandle));
        connection.Start();
    }

    private void Finished(Connection connection)
    {
        lock (_gate)
        {
            _connections.Remove(connection);
        }

        Interlocked.Decrement(ref _open);
    }

    /// <summary>Une connexion relayée : deux copies d'octets, une par sens, jusqu'à ce que les deux soient finies.</summary>
    private sealed class Connection(LocalRelay owner, TcpClient tcp, AnonymousPipeServerStream toModule, AnonymousPipeServerStream fromModule)
    {
        private int _closed;

        public void Start()
        {
            var toTheModule = Task.Run(PumpToModuleAsync);
            var fromTheModule = Task.Run(PumpFromModuleAsync);
            _ = Task.WhenAll(toTheModule, fromTheModule).ContinueWith(_ =>
            {
                Close();
                owner.Finished(this);
            }, TaskScheduler.Default);
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return;
            }

            // Libérer le socket réveille la copie du réseau ; celle du tube se réveille quand l'hôte ferme ses poignées.
            tcp.Dispose();
            toModule.Dispose();
            fromModule.Dispose();
        }

        // Le réseau vers le module. Quand le serveur ferme, le module lit la fin du flux.
        private async Task PumpToModuleAsync()
        {
            try
            {
                await tcp.GetStream().CopyToAsync(toModule).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or InvalidOperationException)
            {
                // le serveur ou le module est parti
            }
            finally
            {
                try
                {
                    toModule.Dispose();
                }
                catch (IOException)
                {
                    // déjà fermé
                }
            }
        }

        // Le module vers le réseau. Quand le module ferme son écriture, le serveur reçoit la fin du flux (demi-fermeture).
        private async Task PumpFromModuleAsync()
        {
            try
            {
                await fromModule.CopyToAsync(tcp.GetStream()).ConfigureAwait(false);
                tcp.Client.Shutdown(SocketShutdown.Send);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or InvalidOperationException)
            {
                // le serveur ou le module est parti
            }
        }
    }
}

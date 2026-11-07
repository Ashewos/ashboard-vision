using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Trofeo.Settings.Services;

/// <summary>
/// Une seule fenêtre de réglage à la fois (l'icône de notification relance l'exécutable à chaque clic). Le premier
/// lancement prend un mutex nommé ; un second lancement le constate, prévient le premier par un <see cref="EventWaitHandle"/>
/// nommé (Windows seulement : ailleurs, ces objets nommés n'existent pas), puis doit quitter.
/// <para>
/// Un double-clic sur un module (<c>.avmod</c>) pendant que la fenêtre est déjà ouverte lance un second exécutable avec le chemin du
/// fichier : il le passe au premier par un tube nommé, réservé à l'utilisateur courant (<see cref="PipeOptions.CurrentUserOnly"/>,
/// comme <c>Trofeo.Control</c>). Le premier lancement traite ce qu'il reçoit comme sa propre ligne de commande : ce ne sont que des
/// chemins de fichiers <c>.avmod</c>, qui repassent par <see cref="PackageArgument"/> (disque local, fichier existant), et rien ne
/// s'installe sans le clic sur « Installer ».
/// </para>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string MutexName = @"Local\trofeo-HB-dashboard.settings";
    public const string ShowEventName = @"Local\trofeo-HB-dashboard.settings.show";

    /// <summary>
    /// Le tube par lequel un second lancement passe le chemin d'un <c>.avmod</c> au premier (sans le préfixe <c>\\.\pipe\</c>). Le nom porte
    /// l'identifiant de la session Windows, comme la portée <c>Local\</c> du mutex : deux sessions ouvertes en même temps (changement
    /// rapide d'utilisateur, services Bureau à distance) ont chacune leur fenêtre et leur tube, et un tube ne prend jamais la place de l'autre.
    /// </summary>
    public static string PipeName
    {
        get
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return PipeNameForSession(process.SessionId);
        }
    }

    /// <summary>Le nom du tube de la session Windows donnée.</summary>
    public static string PipeNameForSession(int sessionId) => "trofeo-HB-dashboard.settings.open." + sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // Un double-clic donne un seul chemin ; quelques-uns au plus, et 64 Ko en tout : un tube qui déborde est ignoré.
    private const int MaxArguments = 4;
    private const int MaxMessageBytes = 64 * 1024;
    // Le tube ne peut pas être créé (nom pris par un autre processus de l'utilisateur, droits) : quelques essais, puis on abandonne.
    private const int MaxPipeCreationFailures = 5;
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);
    private const int SendTimeoutMilliseconds = 3000;

    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _show;
    private readonly ManualResetEvent? _stop;
    private readonly Thread? _waiter;
    private readonly CancellationTokenSource? _serverStop;
    private readonly Task? _server;

    private readonly object _gate = new();
    private Action<string[]>? _argumentsReceived;
    private string[]? _pending;

    private SingleInstance(Mutex mutex, bool isFirst, EventWaitHandle? show, string pipeName)
    {
        _mutex = mutex;
        IsFirst = isFirst;
        _show = show;
        if (isFirst && show is not null)
        {
            _stop = new ManualResetEvent(false);
            _waiter = new Thread(WaitForSecondLaunch) { IsBackground = true, Name = "Réglages : second lancement" };
            _waiter.Start();

            _serverStop = new CancellationTokenSource();
            var token = _serverStop.Token;
            _server = Task.Run(() => ServeAsync(pipeName, token));
        }
    }

    /// <summary>Vrai pour le premier lancement. Un second lancement a déjà prévenu le premier et doit quitter.</summary>
    public bool IsFirst { get; }

    /// <summary>Vrai tant que le tube des fichiers <c>.avmod</c> est servi (faux pour un second lancement, après <see cref="Dispose"/>, ou si le tube n'a pas pu être créé).</summary>
    public bool IsListeningForFiles => _server is { IsCompleted: false };

    /// <summary>Un second lancement demande de ramener la fenêtre au premier plan. Levé sur un fil d'arrière-plan.</summary>
    public event Action? ShowRequested;

    /// <summary>
    /// Un second lancement a passé des chemins de fichiers <c>.avmod</c> (la ligne de commande telle qu'il l'a reçue, chemins complets).
    /// Levé sur un fil d'arrière-plan. Un message reçu avant le premier abonné n'est pas perdu : il est rendu à l'abonnement.
    /// </summary>
    public event Action<string[]>? ArgumentsReceived
    {
        add
        {
            string[]? pending;
            lock (_gate)
            {
                _argumentsReceived += value;
                pending = _pending;
                _pending = null;
            }

            if (pending is not null && value is not null)
                Invoke(value, pending);
        }
        remove
        {
            lock (_gate)
                _argumentsReceived -= value;
        }
    }

    /// <param name="arguments">
    /// La ligne de commande de ce lancement. Si c'est un second lancement, les chemins <c>.avmod</c> qu'elle contient sont passés au premier.
    /// </param>
    public static SingleInstance Acquire(string mutexName = MutexName, string showEventName = ShowEventName, string? pipeName = null, IReadOnlyList<string>? arguments = null)
    {
        pipeName ??= PipeName;
        // Pas de propriété du mutex (premier paramètre faux) : il n'y a rien à libérer, et la sortie du processus suffit.
        var mutex = new Mutex(false, mutexName, out var createdNew);
        var show = OperatingSystem.IsWindows() ? new EventWaitHandle(false, EventResetMode.AutoReset, showEventName) : null;
        if (!createdNew)
        {
            // Le second lancement vient d'être cliqué : il a le droit de donner le premier plan à la fenêtre déjà ouverte.
            AllowForeground();
            show?.Set();
            if (show is not null && arguments is { Count: > 0 })
                SendArguments(pipeName, arguments);
        }

        return new SingleInstance(mutex, createdNew, show, pipeName);
    }

    private void WaitForSecondLaunch()
    {
        try
        {
            while (WaitHandle.WaitAny([_show!, _stop!]) == 0)
            {
                try { ShowRequested?.Invoke(); }
                catch { /* un abonné défaillant n'arrête pas l'écoute */ }
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // ---- Le tube : un chemin de fichier d'un second lancement vers le premier ----

    /// <summary>Second lancement : envoie au premier les arguments qui sont des fichiers <c>.avmod</c> (chemins complets, un par ligne). Un échec est sans suite.</summary>
    private static void SendArguments(string pipeName, IReadOnlyList<string> arguments)
    {
        var paths = new List<string>();
        foreach (var raw in arguments)
        {
            var arg = raw?.Trim().Trim('"').Trim();
            if (string.IsNullOrEmpty(arg) || !arg.EndsWith(".avmod", StringComparison.OrdinalIgnoreCase) || arg.Contains('\n'))
                continue;

            // Le dossier courant de ce lancement n'est pas celui du premier : un chemin relatif n'a de sens qu'ici. (Aucun accès au disque.)
            try { arg = Path.GetFullPath(arg); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }

            paths.Add(arg);
            if (paths.Count == MaxArguments)
                break;
        }

        if (paths.Count == 0)
            return;

        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(SendTimeoutMilliseconds);
            var bytes = new UTF8Encoding(false).GetBytes(string.Join('\n', paths));
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Le premier ne répond pas : il vient quand même d'être ramené au premier plan.
        }
    }

    /// <summary>Premier lancement : accepte un client à la fois, lit au plus 64 Ko en 5 s, et transmet les chemins reçus.</summary>
    private async Task ServeAsync(string pipeName, CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                failures = 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Pas de boucle silencieuse : quelques essais espacés, puis on abandonne. Le second lancement ramène quand même la fenêtre
                // (l'événement « show »), sans pouvoir lui passer le chemin : c'est le comportement d'avant le tube.
                if (++failures >= MaxPipeCreationFailures)
                {
                    System.Diagnostics.Trace.TraceWarning("Réglages : le tube de passage des fichiers .avmod n'a pas pu être créé (" + ex.Message + ") ; un second lancement ne pourra pas transmettre son fichier.");
                    return;
                }

                try { await Task.Delay(200 * failures, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            try
            {
                await using var owner = pipe.ConfigureAwait(false);
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ReceiveTimeout);
                if (await ReadMessageAsync(pipe, timeout.Token).ConfigureAwait(false) is { } text)
                    Deliver(text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(MaxArguments).ToArray());
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or ObjectDisposedException or DecoderFallbackException)
            {
                // Un client muet (délai dépassé), qui part avant d'avoir fini, ou un message qui n'est pas du texte : on passe au suivant, sans boucler à vide.
                try { await Task.Delay(100, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    // Le message entier ou rien : au-delà de MaxMessageBytes, c'est que le client n'est pas un second lancement.
    private static async Task<string?> ReadMessageAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxMessageBytes + 1];
        var length = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
            if (read == 0)
                break;
            length += read;
            if (length > MaxMessageBytes)
                return null;
        }

        return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(buffer, 0, length);
    }

    internal void Deliver(string[] arguments)
    {
        if (arguments.Length == 0)
            return;

        Action<string[]>? handlers;
        lock (_gate)
        {
            handlers = _argumentsReceived;
            if (handlers is null)
            {
                // Pas encore d'abonné (la fenêtre se monte) : le message attend le premier.
                _pending = arguments;
                return;
            }
        }

        Invoke(handlers, arguments);
    }

    private static void Invoke(Action<string[]> handlers, string[] arguments)
    {
        try { handlers(arguments); }
        catch { /* un abonné défaillant n'arrête pas l'écoute */ }
    }

    public void Dispose()
    {
        _stop?.Set();
        _serverStop?.Cancel();
        _waiter?.Join(TimeSpan.FromSeconds(1));
        try { _server?.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException) { }
        _stop?.Dispose();
        _show?.Dispose();
        _serverStop?.Dispose();
        _mutex.Dispose();
    }

    private static void AllowForeground()
    {
        if (!OperatingSystem.IsWindows())
            return;
        try { AllowSetForegroundWindow(-1); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}

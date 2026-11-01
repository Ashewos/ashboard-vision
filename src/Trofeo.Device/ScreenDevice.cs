using System.Diagnostics;

namespace Trofeo.Device;

/// <summary>
/// L'écran : poignée de main, envoi d'une image à la fois avec validation, et garde-fou de reprise
/// quand l'écran ne répond plus. Utilisé depuis un seul thread (la boucle d'affichage) ; seul
/// <see cref="Status"/> se lit depuis un autre thread.
/// </summary>
public sealed class ScreenDevice : IDisposable
{
    public const string AbsentReason = "Écran non branché";
    public const string BusyReason = "Écran utilisé par un autre programme (ferme TRCC)";
    public const string NotRespondingReason = "Écran ne répond plus";

    /// <summary>Intervalle minimal entre deux scans USB (écran Thermalright non pris en charge) et entre deux essais d'un écran au modèle non pris en charge.</summary>
    private static readonly TimeSpan SlowRetryInterval = TimeSpan.FromSeconds(30);

    private const int WriteBlockSize = 4096;
    private const int WriteTimeoutMs = 5000;
    private const int AckSize = 512;
    private const int AckTimeoutMs = 1000;
    private const int HandshakeTimeoutMs = 1000;
    private const int DrainTimeoutMs = 300;
    private const int MaxDrainReads = 64; // garde-fou : un écran qui répond sans fin ne bloque pas la reprise

    private readonly IUsbTransportFactory _factory;
    private readonly TimeProvider _time;
    private readonly Func<IReadOnlyList<(ushort Vid, ushort Pid)>> _scanPresentUsb;
    private IUsbTransport? _transport;
    private ScreenStatus _status = new(ScreenState.Disconnected, AbsentReason);
    private long? _unsupportedAt; // début de la dernière tentative qui a trouvé un modèle non pris en charge
    private long? _lastScanAt;
    private string _absentReason = AbsentReason; // motif du dernier scan, gardé entre deux scans

    /// <param name="scanPresentUsb">
    /// Liste les VID:PID des périphériques USB présents, pour nommer un écran Thermalright non pris en charge quand
    /// aucun écran ne s'ouvre. Par défaut <see cref="UsbPresenceScanner.Scan"/> sous Windows, une liste vide ailleurs.
    /// </param>
    public ScreenDevice(IUsbTransportFactory factory, TimeProvider time, Func<IReadOnlyList<(ushort Vid, ushort Pid)>>? scanPresentUsb = null)
    {
        _factory = factory;
        _time = time;
        _scanPresentUsb = scanPresentUsb ?? (OperatingSystem.IsWindows() ? UsbPresenceScanner.Scan : static () => []);
    }

    /// <summary>Motif d'un écran dont le modèle (<c>64 + octet 20</c> de la poignée de main) n'est pas pris en charge.</summary>
    public static string UnsupportedModelReason(int model) =>
        $"Ce modèle d'écran Thermalright (code {model}) n'est pas encore pris en charge : seul le Trofeo Vision 9,16 pouces (1920 × 462) l'est pour l'instant.";

    /// <summary>État et motif, remplacés d'un bloc : sûr à lire depuis n'importe quel thread (icône de la zone de notification).</summary>
    public ScreenStatus Status => Volatile.Read(ref _status);

    /// <summary>Raccourci vers <c>Status.State</c>. Pour lire l'état et le motif ensemble, passer par <see cref="Status"/>.</summary>
    public ScreenState State => Status.State;

    /// <summary>Raccourci vers <c>Status.Reason</c> : motif en français quand l'état est <see cref="ScreenState.Disconnected"/>, sinon <c>null</c>.</summary>
    public string? Reason => Status.Reason;

    /// <summary>Copie de la dernière image envoyée et validée par l'écran.</summary>
    public byte[]? LastFrame { get; private set; }

    public event EventHandler? StateChanged;

    /// <summary>Ouvre l'écran, fait la poignée de main, puis renvoie <see cref="LastFrame"/> s'il existe.</summary>
    public bool TryConnect()
    {
        if (State == ScreenState.Ready)
            return true;

        // Modèle non pris en charge : on ne rouvre pas l'écran à chaque essai, le motif reste affiché.
        if (_unsupportedAt is { } unsupportedAt && _time.GetElapsedTime(unsupportedAt) < SlowRetryInterval)
            return false;

        _unsupportedAt = null;
        DropTransport();
        IUsbTransport? transport;
        try
        {
            transport = _factory.TryOpen();
        }
        catch (UsbBusyException)
        {
            SetState(ScreenState.Disconnected, BusyReason);
            return false;
        }

        if (transport is null)
        {
            SetState(ScreenState.Disconnected, ReasonWhenNoScreenOpens());
            return false;
        }

        _transport = transport;
        int? model;
        try
        {
            model = TryResetAndHandshake(transport);
        }
        catch (IOException)
        {
            Disconnect(AbsentReason);
            return false;
        }

        if (model is null)
        {
            Disconnect(NotRespondingReason);
            return false;
        }

        if (model != LyProtocol.SupportedModel)
        {
            _unsupportedAt = _time.GetTimestamp();
            Disconnect(UnsupportedModelReason(model.Value));
            return false;
        }

        // Ready n'est annoncé qu'une fois la dernière image renvoyée : pas de Ready -> Disconnected.
        if (LastFrame is not null && !TrySend(transport, LastFrame))
            return false;

        SetState(ScreenState.Ready, null);
        return true;
    }

    /// <summary>
    /// Envoie une image et attend la validation. Sur délai dépassé, lance la reprise puis retente
    /// une seule fois. Renvoie faux si l'écran n'est pas prêt ou si la reprise échoue.
    /// </summary>
    public bool TrySendFrame(byte[] jpeg)
    {
        if (_transport is null || State != ScreenState.Ready)
            return false;

        if (!TrySend(_transport, jpeg))
            return false;

        LastFrame = jpeg.ToArray(); // copie : l'appelant peut réutiliser son tampon
        SetState(ScreenState.Ready, null);
        return true;
    }

    /// <summary>Vrai si l'écran est branché (sans l'ouvrir).</summary>
    public bool IsPresent() => _factory.IsPresent();

    /// <summary>
    /// Abandonne la connexion (écran disparu, reprise après veille) : libère le transport et passe en
    /// Disconnected avec ce motif. Sans effet si l'écran est déjà déconnecté.
    /// </summary>
    public void MarkLost(string reason)
    {
        if (State == ScreenState.Disconnected)
            return;

        Disconnect(reason);
    }

    public void Dispose() => DropTransport();

    /// <summary>
    /// Envoi avec reprise. Sur échec, libère le transport et passe en Disconnected ; sur succès,
    /// l'appelant fixe l'état final.
    /// </summary>
    private bool TrySend(IUsbTransport transport, byte[] jpeg)
    {
        var packets = LyProtocol.Packetize(jpeg);
        try
        {
            try
            {
                SendPackets(transport, packets);
            }
            catch (UsbTimeoutException)
            {
                SetState(ScreenState.Recovering, null);
                if (TryResetAndHandshake(transport) is null)
                {
                    Disconnect(NotRespondingReason);
                    return false;
                }

                SendPackets(transport, packets);
            }
        }
        catch (UsbTimeoutException)
        {
            Disconnect(NotRespondingReason);
            return false;
        }
        catch (IOException)
        {
            Disconnect(AbsentReason);
            return false;
        }

        return true;
    }

    private static void SendPackets(IUsbTransport transport, byte[] packets)
    {
        for (var offset = 0; offset < packets.Length; offset += WriteBlockSize)
        {
            var length = Math.Min(WriteBlockSize, packets.Length - offset);
            transport.Write(packets.AsSpan(offset, length), WriteTimeoutMs);
        }

        transport.Read(new byte[AckSize], AckTimeoutMs);
    }

    /// <summary>
    /// Reset des pipes, vidage des réponses en attente, poignée de main. Sert à la reprise et à chaque
    /// connexion : un processus précédent a pu figer l'écran en laissant une validation en file.
    /// Renvoie le code du modèle, ou <c>null</c> si l'écran ne répond pas comme il faut.
    /// </summary>
    private static int? TryResetAndHandshake(IUsbTransport transport)
    {
        transport.ResetPipes();
        var drain = new byte[AckSize];
        try
        {
            for (var i = 0; i < MaxDrainReads; i++)
                transport.Read(drain, DrainTimeoutMs);
        }
        catch (UsbTimeoutException)
        {
            // La file est vide : c'est le résultat attendu.
        }

        return TryHandshake(transport);
    }

    private static int? TryHandshake(IUsbTransport transport)
    {
        try
        {
            transport.Write(LyProtocol.BuildHandshake(), HandshakeTimeoutMs);
            var reply = new byte[AckSize];
            var read = transport.Read(reply, HandshakeTimeoutMs);
            var received = reply.AsSpan(0, read);
            return LyProtocol.IsValidHandshakeReply(received) ? LyProtocol.ReadModel(received) : null;
        }
        catch (UsbTimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// Aucun écran pris en charge ne s'ouvre : cherche, au plus toutes les 30 s, un écran Thermalright connu
    /// mais non pris en charge. Entre deux scans, le motif du dernier reste affiché.
    /// </summary>
    private string ReasonWhenNoScreenOpens()
    {
        if (_lastScanAt is { } lastScanAt && _time.GetElapsedTime(lastScanAt) < SlowRetryInterval)
            return _absentReason;

        _lastScanAt = _time.GetTimestamp();
        _absentReason = AbsentReason;
        foreach (var (vid, pid) in _scanPresentUsb())
        {
            if (KnownThermalrightDevices.Describe(vid, pid) is not { } family)
                continue;

            _absentReason =
                $"Écran Thermalright détecté ({family}) : il n'est pas encore pris en charge (par exemple {KnownThermalrightDevices.Examples(vid, pid)}). "
                + "Seul le Trofeo Vision 9,16 pouces (1920 × 462) l'est pour l'instant.";
            break;
        }

        return _absentReason;
    }

    private void Disconnect(string reason)
    {
        DropTransport();
        SetState(ScreenState.Disconnected, reason);
    }

    private void DropTransport()
    {
        _transport?.Dispose();
        _transport = null;
    }

    private void SetState(ScreenState state, string? reason)
    {
        var status = new ScreenStatus(state, reason);
        if (Status == status)
            return;

        Volatile.Write(ref _status, status);
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // Un abonné défaillant ne doit pas casser la machine d'états.
            Trace.TraceError($"Un abonné de StateChanged a échoué : {ex}");
        }
    }
}

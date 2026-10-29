using System.Reflection;
using Trofeo.ModuleKit;

namespace Trofeo.Modules.Network;

/// <summary>
/// Débit d'une Freebox, lu par son API locale. La première fois, la box demande d'appuyer sur ► sur sa façade pour
/// autoriser l'application ; le jeton obtenu est gardé dans le coffre à secrets, puis chaque lecture ouvre une
/// session (une fois) et lit <c>connection/</c>. Appelée par le module sans chevauchement, donc sans verrou : l'état
/// (préfixe découvert, demande d'autorisation, session) survit aux exceptions, et la lecture suivante reprend.
/// </summary>
public sealed class FreeboxSource : IThroughputSource
{
    internal const string TokenKey = "freebox.app_token";

    internal const string AppId = "trofeo-HB-dashboard";
    private const string AppName = "Ashboard Vision";
    private const int MinMajorVersion = 4;

    private const string ButtonMessage = "Appuie sur ► sur ta Freebox pour autoriser le dashboard";
    private const string DeniedMessage = "Autorisation refusée sur la Freebox";
    private const string TooOldMessage = "Freebox trop ancienne";

    private static readonly TimeSpan ConnectedInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AuthorizingInterval = TimeSpan.FromSeconds(2);

    /// <summary>Attente avant de redemander l'autorisation après un refus sur la box.</summary>
    private static readonly TimeSpan DeniedWait = TimeSpan.FromMinutes(5);

    private readonly ISecretStore _secrets;
    private readonly IModuleLog _log;
    private readonly TimeProvider _time;
    private readonly FreeboxApi _api;

    private string? _prefix;
    private string? _appToken;
    private bool _appTokenLoaded;
    private string? _pendingToken;
    private long? _trackId;
    private string? _session;
    private long? _deniedAt;
    private string? _lastWarning;
    private volatile bool _connected;

    public FreeboxSource(HttpClient http, ISecretStore secrets, IModuleLog log, Uri? baseUri = null, TimeProvider? time = null)
    {
        _secrets = secrets;
        _log = log;
        _time = time ?? TimeProvider.System;
        _api = new FreeboxApi(http, baseUri ?? new Uri("http://mafreebox.freebox.fr"), _time);
    }

    /// <summary>Version de l'application annoncée à la box : celle de l'assembly, sans les métadonnées de build.</summary>
    internal static string AppVersion { get; } = ReadAppVersion();

    public string Title => "Freebox";

    /// <summary>
    /// 1 s une fois que la dernière lecture a donné un état de connexion validé, 2 s tant que l'autorisation, le refus
    /// ou une panne (lecture qui lève, session refusée) durent.
    /// </summary>
    public TimeSpan Interval => _connected ? ConnectedInterval : AuthorizingInterval;

    public async Task<SourceReading> ReadAsync(CancellationToken ct)
    {
        try
        {
            return await ReadCoreAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            _connected = false;
            throw;
        }
    }

    private async Task<SourceReading> ReadCoreAsync(CancellationToken ct)
    {
        if (_deniedAt is { } deniedAt)
        {
            if (_time.GetElapsedTime(deniedAt) < DeniedWait) return Denied();
            _deniedAt = null;
        }

        // 1. Découverte.
        if (_prefix is null)
        {
            var info = await _api.DiscoverAsync(ct).ConfigureAwait(false);
            if (info.Major < MinMajorVersion)
            {
                _connected = false;
                return new SourceReading(null, new ModuleStatus(ModuleHealth.Offline, TooOldMessage));
            }
            _prefix = $"{info.BaseUrl}v{info.Major}/";
        }

        var token = GetToken();
        if (token is null)
        {
            // 2. Sans jeton : demande d'autorisation, puis suivi à chaque lecture.
            if (_trackId is null) return await RequestAuthorizationAsync(ct).ConfigureAwait(false);

            // 3. Suivi de l'autorisation.
            if (await FollowAuthorizationAsync(ct).ConfigureAwait(false) is { } waiting) return waiting;
            token = GetToken();
            if (token is null) throw new FreeboxException("Jeton Freebox introuvable après l'autorisation.");
        }
        else
        {
            ForgetAuthorizationRequest();
        }

        return await ReadRatesAsync(token, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
    }

    // --- Autorisation ---

    /// <summary>Jeton d'application : lu une fois dans le coffre, puis gardé en mémoire (voir <see cref="SetToken"/>).</summary>
    private string? GetToken()
    {
        if (!_appTokenLoaded)
        {
            _appToken = _secrets.Get(TokenKey);
            _appTokenLoaded = true;
        }
        return _appToken;
    }

    /// <summary>Enregistre (ou efface, avec <c>null</c>) le jeton dans le coffre et dans la copie en mémoire.</summary>
    private void SetToken(string? token)
    {
        _secrets.Set(TokenKey, token);
        _appToken = token;
        _appTokenLoaded = true;
    }

    private async Task<SourceReading> RequestAuthorizationAsync(CancellationToken ct)
    {
        var body = new { app_id = AppId, app_name = AppName, app_version = AppVersion, device_name = Environment.MachineName };
        var response = await _api.PostAsync(_prefix + "login/authorize/", body, ct).ConfigureAwait(false);
        if (!response.Success) throw Refused("demande d'autorisation", response);

        var appToken = response.GetString("app_token");
        var trackId = response.GetNumber("track_id");
        if (appToken is null || trackId is null) throw new FreeboxException("Réponse d'autorisation de la Freebox incomplète.");

        _pendingToken = appToken;
        _trackId = (long)trackId;
        _connected = false;
        return Waiting();
    }

    /// <summary>Renvoie l'état à afficher tant que l'autorisation n'est pas accordée, ou <c>null</c> une fois le jeton enregistré.</summary>
    private async Task<SourceReading?> FollowAuthorizationAsync(CancellationToken ct)
    {
        var response = await _api.GetAsync($"{_prefix}login/authorize/{_trackId}", null, ct).ConfigureAwait(false);
        if (!response.Success)
        {
            // Demande inconnue de la box (redémarrée, par exemple) : on en refait une.
            ForgetAuthorizationRequest();
            return await RequestAuthorizationAsync(ct).ConfigureAwait(false);
        }

        switch (response.GetString("status"))
        {
            case "granted":
                SetToken(_pendingToken);
                ForgetAuthorizationRequest();
                _log.Info("Autorisation accordée par la Freebox.");
                return null;

            case "timeout" or "unknown":
                ForgetAuthorizationRequest();
                return await RequestAuthorizationAsync(ct).ConfigureAwait(false);

            case "denied":
                ForgetAuthorizationRequest();
                _deniedAt = _time.GetTimestamp();
                _connected = false;
                _log.Warning("Autorisation refusée sur la Freebox ; nouvelle demande dans 5 minutes.");
                return Denied();

            default: // « pending »
                _connected = false;
                return Waiting();
        }
    }

    private void ForgetAuthorizationRequest()
    {
        _pendingToken = null;
        _trackId = null;
    }

    private static SourceReading Waiting() =>
        new(null, new ModuleStatus(ModuleHealth.NotConfigured, ButtonMessage));

    private static SourceReading Denied() =>
        new(null, new ModuleStatus(ModuleHealth.NotConfigured, DeniedMessage));

    // --- Session et débit ---

    private async Task<SourceReading> ReadRatesAsync(string token, CancellationToken ct)
    {
        if (_session is null && await LoginAsync(token, ct).ConfigureAwait(false) is { } restarted) return restarted;

        var response = await _api.GetAsync(_prefix + "connection/", _session, ct).ConfigureAwait(false);
        if (response.IsError("auth_required"))
        {
            // Session expirée : une seule nouvelle ouverture, puis une seule nouvelle lecture.
            _session = null;
            if (await LoginAsync(token, ct).ConfigureAwait(false) is { } relogRestarted) return relogRestarted;

            response = await _api.GetAsync(_prefix + "connection/", _session, ct).ConfigureAwait(false);
            if (!response.Success)
            {
                _session = null;
                _connected = false;
                Warn($"Lecture du débit refusée par la Freebox malgré une nouvelle session ({response.ErrorCode ?? "erreur inconnue"}).");
                return new SourceReading(null, new ModuleStatus(ModuleHealth.Offline));
            }
        }
        else if (!response.Success)
        {
            throw Refused("lecture du débit", response);
        }

        if (response.GetString("state") is not { } state) throw new FreeboxException("État de la connexion Freebox illisible.");
        _lastWarning = null; // la box répond de nouveau : la prochaine panne sera signalée
        if (state != "up")
        {
            // La box répond et son état est lisible : la connexion est établie avec elle, seule la ligne est coupée.
            _connected = true;
            return new SourceReading(new ThroughputSample(0, 0), new ModuleStatus(ModuleHealth.Offline));
        }

        if (response.GetNumber("rate_down") is not { } down || response.GetNumber("rate_up") is not { } up)
        {
            throw new FreeboxException("Débit de la Freebox illisible.");
        }
        _connected = true;
        return new SourceReading(new ThroughputSample(down, up), new ModuleStatus(ModuleHealth.Online));
    }

    /// <summary>Ouvre une session. Renvoie l'état à afficher si la box a refusé le jeton (nouvelle autorisation), sinon <c>null</c>.</summary>
    private async Task<SourceReading?> LoginAsync(string token, CancellationToken ct)
    {
        var challengeResponse = await _api.GetAsync(_prefix + "login/", null, ct).ConfigureAwait(false);
        if (!challengeResponse.Success) throw Refused("demande de défi", challengeResponse);
        var challenge = challengeResponse.GetString("challenge") ?? throw new FreeboxException("Défi de la Freebox illisible.");

        var body = new { app_id = AppId, password = FreeboxAuth.Password(token, challenge) };
        var response = await _api.PostAsync(_prefix + "login/session/", body, ct).ConfigureAwait(false);
        if (response.Success)
        {
            _session = response.GetString("session_token") ?? throw new FreeboxException("Session de la Freebox illisible.");
            return null;
        }

        if (response.ErrorCode is "invalid_token" or "pending_token")
        {
            SetToken(null);
            _session = null;
            ForgetAuthorizationRequest();
            _connected = false;
            _log.Warning("Jeton refusé par la Freebox ; nouvelle autorisation nécessaire.");
            return await RequestAuthorizationAsync(ct).ConfigureAwait(false);
        }

        throw Refused("ouverture de session", response);
    }

    /// <summary>Journalise une fois par épisode : la même alerte n'est pas répétée à chaque lecture.</summary>
    private void Warn(string message)
    {
        if (message == _lastWarning) return;
        _lastWarning = message;
        _log.Warning(message);
    }

    private static FreeboxException Refused(string step, FreeboxResponse response) =>
        new($"Freebox : {step} refusée ({response.ErrorCode ?? "erreur inconnue"}).");

    private static string ReadAppVersion()
    {
        var assembly = typeof(FreeboxSource).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";
        var metadata = version.IndexOf('+');
        return metadata > 0 ? version[..metadata] : version;
    }
}

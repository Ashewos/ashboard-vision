using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AshboardVision.Sdk;
using SkiaSharp;
using Trofeo.ModuleKit;

namespace Minecraft;

/// <summary>
/// Un module qui parle à un serveur de jeu : il affiche l'icône, le message du jour, les joueurs connectés et la latence d'un serveur Minecraft Java.
/// <para>
/// Il montre ce que fait un module qui se connecte à autre chose qu'un site web : lire son adresse et son port dans ses réglages, choisir entre la
/// connexion « Cet ordinateur » (un serveur lancé sur ce PC, que le logiciel relaie) et une connexion TCP ordinaire (un serveur du réseau local ou
/// d'Internet), distinguer les pannes pour dire à l'utilisateur quoi faire, et se fermer proprement même au milieu d'une requête.
/// </para>
/// </summary>
public class MinecraftModule : CommunityModule
{
    /// <summary>Toutes les 30 secondes : assez pour voir arriver les joueurs, sans harceler le serveur.</summary>
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    private const int DefaultPort = 25565;
    private const string Unreachable = "Serveur injoignable";

    /// <summary>Une icône de serveur fait 64 × 64 ; au-delà de ce côté, l'image est ignorée (un PNG minuscule peut décompresser en gigaoctets).</summary>
    private const int MaxIconSide = 256;

    // Un nom de machine ou une adresse (IPv4, ou IPv6 entre crochets), sans espace ni caractère étrange.
    private static readonly Regex Address = new(@"^[\p{L}\p{N}._:\[\]-]{1,100}$", RegexOptions.CultureInvariant);

    private readonly CancellationTokenSource _stop = new();
    private string _host = "";
    private int _port = DefaultPort;
    private bool _local;
    private ITimer? _timer;
    private volatile bool _closed;
    private volatile Task _refresh = Task.CompletedTask;
    private volatile ServerStatus? _status;

    // L'icône décodée : lue et remplacée par Render seulement (jamais en parallèle avec lui-même), libérée à la fermeture, après le dernier dessin.
    private byte[]? _iconSource;
    private SKImage? _icon;

    /// <summary>
    /// Ce que le module a appris du serveur à la dernière actualisation réussie (<c>null</c> avant la première). Interne : les tests lisent ce que le
    /// module a retenu, au lieu de le deviner sur l'image.
    /// </summary>
    internal ServerStatus? Status => _status;

    /// <summary>L'actualisation en cours ou la dernière (terminée). Interne : les tests l'attendent au lieu de sonder.</summary>
    internal Task LastRefresh => _refresh;

    /// <summary>Le délai d'une interrogation du serveur ; les tests le raccourcissent pour ne pas attendre cinq secondes un serveur muet.</summary>
    internal virtual TimeSpan QueryTimeout => TimeSpan.FromSeconds(5);

    /// <summary>
    /// Ouvre la connexion vers un serveur qui n'est pas sur ce PC. C'est le point d'entrée des tests : une sous-classe du projet de tests (qui voit ce
    /// membre grâce à <c>InternalsVisibleTo</c>) le redéfinit pour joindre un faux serveur. Le module n'ouvre jamais de connexion vers <c>127.0.0.1</c>
    /// lui-même : enfermé, il n'en a pas le droit (voir <see cref="StartAsync"/>).
    /// </summary>
    internal virtual Task<Stream> ConnectRemoteAsync(string host, int port, CancellationToken ct) =>
        ServerListPing.ConnectAsync(host, port, ConnectTimeout, ct);

    /// <summary>
    /// Lit les réglages « Adresse » et « Port », vérifie les permissions puis lance la première actualisation et la minuterie. Rien n'est attendu
    /// ici : <c>StartAsync</c> reste rapide, et l'image arrive quand le serveur a répondu.
    /// </summary>
    public override Task StartAsync(CancellationToken ct)
    {
        var settings = Services.Settings;
        // « localhost » n'est que ce que le champ propose (son texte grisé) : un champ vide veut dire que l'utilisateur n'a encore rien choisi.
        var address = ReadText(settings, "adresse");
        if (!Address.IsMatch(address))
        {
            SetStatus(ModuleHealth.NotConfigured, "Indique l'adresse du serveur (un nom ou une adresse, sans espace ; localhost pour un serveur de ce PC) dans les réglages du module.");
            return Task.CompletedTask;
        }

        _host = address.Trim('[', ']'); // une adresse IPv6 s'écrit [::1] ; le protocole veut ::1
        if (!TryReadPort(settings, out _port))
        {
            SetStatus(ModuleHealth.NotConfigured, "Indique le port du serveur, un nombre entre 1 et 65535, dans les réglages du module.");
            return Task.CompletedTask;
        }

        // Windows interdit à un module enfermé de joindre localhost : un serveur de ce PC passe par le relais du logiciel, qui demande la permission
        // « Cet ordinateur ». Les autres serveurs passent par une connexion ordinaire, que les permissions « réseau local » et « Internet » ouvrent.
        // Une permission que l'utilisateur a retirée n'est pas une panne : on le dit, au lieu de laisser une erreur réseau obscure.
        _local = IsThisComputer(_host);
        if (_local && !Services.Has(ModulePermission.ThisComputer))
        {
            SetStatus(ModuleHealth.Offline, "Ce module a besoin de la permission « Cet ordinateur » pour joindre un serveur de ce PC : accorde-la dans la fenêtre de réglage d'Ashboard Vision.");
            return Task.CompletedTask;
        }

        if (!_local && !Services.Has(ModulePermission.LocalNetwork) && !Services.Has(ModulePermission.Internet))
        {
            SetStatus(ModuleHealth.Offline, "Ce module a besoin de la permission « Réseau local » ou « Internet » pour joindre ce serveur : accorde-la dans la fenêtre de réglage d'Ashboard Vision.");
            return Task.CompletedTask;
        }

        Refresh();
        _timer = Services.Time.CreateTimer(_ => Refresh(), null, RefreshEvery, RefreshEvery);
        return Task.CompletedTask;
    }

    /// <summary>Dessine l'icône à gauche, puis les lignes de texte à sa droite, en réduisant le texte pour qu'il tienne dans la case.</summary>
    public override void Render(SKCanvas canvas, ModuleRenderContext ctx)
    {
        // Pas encore de réponse : on ne dessine rien. Le logiciel affiche lui-même le message de l'état (non configuré, hors ligne…).
        if (_status is not { } status)
        {
            return;
        }

        var theme = ctx.Theme;
        var scale = ctx.TextScale;
        var area = SKRect.Inflate(ctx.Bounds, -16f * scale, -12f * scale);
        if (area.Width <= 0f || area.Height <= 0f)
        {
            return;
        }

        var left = area.Left;
        if (IconFor(status.FaviconPng) is { } icon)
        {
            var side = Math.Min(area.Height, area.Width / 3f);
            var target = new SKRect(area.Left, area.MidY - side / 2f, area.Left + side, area.MidY + side / 2f);
            var radius = theme.Shape == ThemeShape.Pixel ? 0f : 8f * scale;
            var sampling = theme.Shape == ThemeShape.Pixel ? new SKSamplingOptions(SKFilterMode.Nearest) : new SKSamplingOptions(SKFilterMode.Linear);
            canvas.Save();
            canvas.ClipRoundRect(new SKRoundRect(target, radius, radius), antialias: true);
            canvas.DrawImage(icon, target, sampling);
            canvas.Restore();
            left = target.Right + 12f * scale;
        }

        var lines = Lines(_host, status, theme);
        var fit = Math.Min(1f, area.Height / (lines.Sum(line => line.Size) * 1.25f * scale));
        var width = area.Right - left;
        if (fit <= 0f || width <= 0f)
        {
            return;
        }

        var top = area.Top;
        foreach (var (text, size, color) in lines)
        {
            using var font = ModuleDrawing.Font(theme, size * scale * fit);
            using var paint = new SKPaint { Color = color, IsAntialias = true };
            ModuleDrawing.DrawText(canvas, ModuleDrawing.Ellipsize(text, font, width), left, top - font.Metrics.Ascent, SKTextAlign.Left, font, paint);
            top += font.Spacing * 1.05f;
        }
    }

    /// <summary>
    /// Les lignes que la case montre, de haut en bas : leur texte, leur taille (avant mise à l'échelle) et leur couleur. Séparé de
    /// <see cref="Render"/> pour que les tests vérifient ce qui est affiché sans lire des pixels. Une ligne sans contenu (aucun pseudo) n'y est pas.
    /// </summary>
    internal static (string Text, float Size, SKColor Color)[] Lines(string address, ServerStatus status, Theme theme)
    {
        var lines = new List<(string, float, SKColor)>
        {
            (status.Motd.Length > 0 ? status.Motd : address, 20f, theme.Highlight),
            ($"{status.Online} / {status.Max} joueurs", 16f, theme.Text),
        };

        // Trois pseudos au plus : la case est petite, et le serveur n'en donne souvent aucun.
        if (status.SamplePlayers.Count > 0)
        {
            lines.Add((string.Join(", ", status.SamplePlayers.Take(3)), 14f, theme.TextMuted));
        }

        var latency = $"{status.Latency.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms";
        lines.Add((status.Version.Length > 0 ? $"{status.Version} · {latency}" : latency, 14f, theme.TextMuted));
        return [.. lines];
    }

    /// <summary>Arrête la minuterie, interrompt l'actualisation en cours et attend sa fin avant de libérer le reste.</summary>
    public override async ValueTask DisposeAsync()
    {
        _closed = true;
        _timer?.Dispose();
        await _stop.CancelAsync().ConfigureAwait(false);
        await _refresh.ConfigureAwait(false);
        _stop.Dispose();
        _icon?.Dispose();
    }

    private static string ReadText(JsonElement settings, string key) =>
        settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!.Trim()
            : "";

    /// <summary>
    /// Le port saisi dans le champ « Port » ; non écrit, c'est la valeur proposée (25565), comme pour le relais du logiciel, qui n'ouvre
    /// que le port de ce champ.
    /// </summary>
    private static bool TryReadPort(JsonElement settings, out int port)
    {
        port = DefaultPort;
        if (settings.ValueKind != JsonValueKind.Object || !settings.TryGetProperty("port", out var written))
        {
            return true;
        }

        return written.ValueKind == JsonValueKind.Number && written.TryGetInt32(out port) && port is >= 1 and <= 65535;
    }

    private static bool IsThisComputer(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host is "127.0.0.1" or "::1";

    /// <summary>
    /// Lance une actualisation, sauf si la précédente dure encore (serveur lent) : il n'y en a jamais deux en même temps. Appelée par la
    /// minuterie, sur un thread du pool : une exception qui en sortirait arrêterait le processus du module, donc aucune n'en sort. Un rappel
    /// qui arrive pendant la fermeture (<c>_stop</c> déjà libéré) est simplement ignoré.
    /// </summary>
    private void Refresh()
    {
        try
        {
            if (!_closed && _refresh.IsCompleted)
            {
                _refresh = RefreshAsync(_stop.Token);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Services.Log.Warning("Actualisation ignorée : " + ex.Message);
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            _status = await QueryServerAsync(ct).ConfigureAwait(false);
            SetStatus(ModuleHealth.Online);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // La case se ferme.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Serveur éteint, qui ne répond pas ou qui répond autre chose : la dernière image reste (le logiciel l'assombrit derrière le
            // message), l'état dit pourquoi, et la prochaine actualisation (dans 30 secondes) réessaie ; si elle réussit, l'état redevient « en ligne ».
            Services.Log.Warning("Actualisation impossible : " + ex.Message);
            SetStatus(ModuleHealth.Offline, ex is ServerProblem ? ex.Message : Unreachable);
        }

        Invalidate();
    }

    private async Task<ServerStatus> QueryServerAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        try
        {
            return await ServerListPing.QueryAsync(connection, _host, _port, QueryTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new ServerProblem("Le serveur ne répond pas à la demande d'état : vérifie l'adresse et le port.", ex);
        }
        catch (InvalidDataException ex)
        {
            throw new ServerProblem("Réponse du serveur illisible : vérifie que c'est bien un serveur Minecraft Java, et pas un autre service sur ce port.", ex);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
        {
            throw new ServerProblem("Le serveur a fermé la connexion sans répondre.", ex);
        }
    }

    /// <summary>
    /// Ouvre la connexion : par le relais du logiciel pour un serveur de ce PC, par une connexion TCP ordinaire pour les autres. Que ce soit un refus,
    /// un délai ou un serveur introuvable, le serveur est « injoignable ».
    /// </summary>
    private async Task<Stream> OpenAsync(CancellationToken ct)
    {
        try
        {
            if (!_local)
            {
                return await ConnectRemoteAsync(_host, _port, ct).ConfigureAwait(false);
            }

            // « Refusé » veut dire ici que rien n'écoute sur ce port : la permission, elle, a déjà été vérifiée au démarrage.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(ConnectTimeout);
            var result = await Services.ConnectLocalAsync(_port, limit.Token).ConfigureAwait(false);
            return result.Status == DataStatus.Ok && result.Value is { } stream ? stream : throw new ServerProblem(Unreachable);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or TimeoutException or IOException or ObjectDisposedException
                                   || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new ServerProblem(Unreachable, ex);
        }
    }

    /// <summary>Un problème dont on connaît la cause, avec la phrase à montrer à l'utilisateur.</summary>
    private sealed class ServerProblem(string message, Exception? inner = null) : Exception(message, inner);

    /// <summary>Décode l'icône quand elle change (une fois par nouvelle réponse, pas à chaque image). Une image trop grande ou illisible est ignorée.</summary>
    private SKImage? IconFor(byte[]? png)
    {
        if (!ReferenceEquals(png, _iconSource))
        {
            _icon?.Dispose();
            _icon = Decode(png);
            _iconSource = png;
        }

        return _icon;
    }

    private static SKImage? Decode(byte[]? png)
    {
        if (png is null)
        {
            return null;
        }

        using var data = SKData.CreateCopy(png);
        using var codec = SKCodec.Create(data);
        return codec is { Info: { Width: >= 1 and <= MaxIconSide, Height: >= 1 and <= MaxIconSide } } ? SKImage.FromEncodedData(data) : null;
    }
}

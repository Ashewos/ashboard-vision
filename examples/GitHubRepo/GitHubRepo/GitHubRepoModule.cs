using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AshboardVision.Sdk;
using SkiaSharp;
using Trofeo.ModuleKit;

namespace GitHubRepo;

/// <summary>
/// Un module qui parle à Internet : il affiche les étoiles, la dernière version et les téléchargements d'un dépôt GitHub.
/// <para>
/// Il montre les quatre choses qu'un module fait presque toujours : lire ses réglages (le dépôt) et un secret (le jeton, facultatif),
/// actualiser ses données à intervalle régulier, annoncer son état (<see cref="ModuleHealth"/>) quand ça ne va pas, et dessiner ce qu'il sait.
/// </para>
/// </summary>
public class GitHubRepoModule : CommunityModule
{
    /// <summary>
    /// Toutes les 10 minutes, soit 6 actualisations par heure. Chacune coûte une requête pour le dépôt, plus une par page de versions
    /// (voir <see cref="MaxReleasePages"/>) : 12 requêtes par heure jusqu'à 99 versions, 36 au plus, sous les 60 que GitHub accorde sans jeton.
    /// </summary>
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Le temps minimal entre deux actualisations lancées par le raccourci « Rafraîchir » : un appui trop proche est ignoré, sauf si la dernière
    /// tentative a échoué sans que GitHub réponde (réseau coupé, délai dépassé) : réessayer tout de suite est alors ce que l'utilisateur veut.
    /// La minuterie n'entre pas dans ce délai.
    /// </summary>
    private static readonly TimeSpan MinGapBetweenManualRefreshes = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Les requêtes qu'on s'accorde par heure glissante, minuterie et raccourci ensemble (GitHub en accorde 60 sans jeton, on garde de la marge).
    /// La minuterie part toujours ; un « Rafraîchir » qui ferait dépasser ce budget est ignoré.
    /// </summary>
    private const int HourlyRequestBudget = 50;

    /// <summary>
    /// GitHub rend les versions par pages de 100. Au plus 5 pages (500 versions) : une actualisation coûte alors 6 requêtes au pire
    /// (36 par heure), sous la limite de 60 sans jeton. Un dépôt qui en a plus voit son total suivi de « 500+ versions ».
    /// </summary>
    private const int ReleasesPerPage = 100;
    private const int MaxReleasePages = 5;

    private static readonly Uri ApiRoot = new("https://api.github.com/");

    // propriétaire/nom : les caractères que GitHub accepte, et un nom qui n'est pas fait que de points (« .. » changerait l'adresse demandée).
    private static readonly Regex RepositoryName = new(@"^[A-Za-z0-9-]+/(?!\.+$)[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);

    private readonly CancellationTokenSource _stop = new();
    private string _repository = "";
    private HttpClient? _http;
    private ITimer? _timer;
    private volatile bool _closed;
    private volatile Task _refresh = Task.CompletedTask;
    private volatile RepoStats? _stats;

    // La minuterie et le raccourci tournent sur des threads différents : ce qui suit se lit et s'écrit par Interlocked / Volatile.
    private RequestBudget? _budget; // les requêtes réellement envoyées pendant l'heure écoulée
    private long _lastManualTicks; // l'instant (UTC) du dernier « Rafraîchir » accepté ; 0 : aucun
    private int _requestsInThisRefresh; // les requêtes de l'actualisation en cours
    private int _requestsPerRefresh = 2; // ce que la dernière actualisation réussie a coûté, pour estimer la suivante (2 au plus bas : dépôt + versions)
    private volatile bool _failedWithoutAnswer; // la dernière tentative a échoué sans réponse de GitHub

    /// <summary>
    /// Le client HTTP du module. C'est le point d'entrée des tests : une sous-classe du projet de tests (qui voit ce membre grâce à
    /// <c>InternalsVisibleTo</c>) le redéfinit pour y glisser de fausses réponses de GitHub.
    /// </summary>
    internal virtual HttpClient CreateHttpClient() => Services.CreateHttpClient();

    /// <summary>
    /// Lit le réglage « Dépôt », vérifie la permission « Internet » puis lance la première actualisation et la minuterie. Rien n'est attendu
    /// ici : <c>StartAsync</c> reste rapide, et l'image arrive quand GitHub a répondu.
    /// </summary>
    public override Task StartAsync(CancellationToken ct)
    {
        _repository = ReadRepository(Services.Settings);
        if (!RepositoryName.IsMatch(_repository))
        {
            SetStatus(ModuleHealth.NotConfigured, "Indique un dépôt GitHub, sous la forme propriétaire/nom, dans les réglages du module.");
            return Task.CompletedTask;
        }

        // Une permission que l'utilisateur a retirée n'est pas une panne : on le dit, au lieu de laisser une erreur réseau obscure.
        if (!Services.Has(ModulePermission.Internet))
        {
            SetStatus(ModuleHealth.Offline, "Ce module a besoin de la permission « Internet » : accorde-la dans la fenêtre de réglage d'Ashboard Vision.");
            return Task.CompletedTask;
        }

        _budget = new RequestBudget(HourlyRequestBudget, TimeSpan.FromHours(1), Services.Time);
        _http = CreateHttpClient();
        _http.BaseAddress = ApiRoot;
        _http.Timeout = TimeSpan.FromSeconds(8);

        Refresh();
        _timer = Services.Time.CreateTimer(_ => Refresh(), null, RefreshEvery, RefreshEvery);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Le logiciel appelle cette méthode quand l'utilisateur utilise le raccourci « Rafraîchir » (<c>refresh</c> dans le manifeste, sans touche
    /// proposée : on lui en donne une dans l'onglet Raccourcis). L'actualisation est la même que celle de la minuterie. Elle est ignorée si le
    /// module n'a rien à actualiser (non configuré, Internet refusé), si une actualisation est déjà en cours, si le dernier appui accepté date de
    /// moins de <see cref="MinGapBetweenManualRefreshes"/> (sauf après une tentative sans réponse de GitHub), ou si elle ferait dépasser le budget
    /// de <see cref="HourlyRequestBudget"/> requêtes par heure (estimé d'après ce que la dernière actualisation a coûté).
    /// </summary>
    public override Task OnShortcutAsync(string shortcutId, CancellationToken ct)
    {
        if (shortcutId == "refresh" && _http is not null && _budget is { } budget && budget.CanAfford(Volatile.Read(ref _requestsPerRefresh)))
        {
            var now = Services.Time.GetUtcNow().UtcTicks;
            var lastManual = Interlocked.Read(ref _lastManualTicks);
            if ((lastManual == 0 || now - lastManual >= MinGapBetweenManualRefreshes.Ticks || _failedWithoutAnswer) && Refresh())
            {
                Interlocked.Exchange(ref _lastManualTicks, now);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Dessine le dépôt, les étoiles, la dernière version et les téléchargements, en réduisant le texte pour qu'il tienne dans la case.</summary>
    public override void Render(SKCanvas canvas, ModuleRenderContext ctx)
    {
        // Pas encore de réponse : on ne dessine rien. Le logiciel affiche lui-même le message de l'état (non configuré, hors ligne…).
        if (_stats is not { } stats)
        {
            return;
        }

        var lines = Lines(_repository, stats, ctx.Theme, Services.Time.LocalTimeZone);
        var scale = ctx.TextScale;
        var area = SKRect.Inflate(ctx.Bounds, -16f * scale, -12f * scale);
        var fit = Math.Min(1f, area.Height / (lines.Sum(line => line.Size) * 1.25f * scale));
        if (fit <= 0f || area.Width <= 0f)
        {
            return;
        }

        var top = area.Top;
        foreach (var (text, size, color) in lines)
        {
            using var font = ModuleDrawing.Font(ctx.Theme, size * scale * fit);
            using var paint = new SKPaint { Color = color, IsAntialias = true };
            ModuleDrawing.DrawText(canvas, ModuleDrawing.Ellipsize(text, font, area.Width), area.Left, top - font.Metrics.Ascent, SKTextAlign.Left, font, paint);
            top += font.Spacing * 1.05f;
        }
    }

    /// <summary>
    /// Les lignes que la case montre, de haut en bas : leur texte, leur taille (avant mise à l'échelle) et leur couleur. Séparé de
    /// <see cref="Render"/> pour que les tests vérifient ce qui est affiché sans lire des pixels.
    /// </summary>
    internal static (string Text, float Size, SKColor Color)[] Lines(string repository, RepoStats stats, Theme theme, TimeZoneInfo zone) =>
    [
        (repository, 16f, theme.TextMuted),
        (stats.StarsText, 36f, theme.Highlight),
        (stats.ReleaseText(zone), 18f, theme.Text),
        (stats.DownloadsText, 18f, theme.TextMuted),
    ];

    /// <summary>Arrête la minuterie, interrompt l'actualisation en cours et attend sa fin avant de fermer le client HTTP.</summary>
    public override async ValueTask DisposeAsync()
    {
        _closed = true;
        _timer?.Dispose();
        await _stop.CancelAsync().ConfigureAwait(false);
        await _refresh.ConfigureAwait(false);
        _http?.Dispose();
        _stop.Dispose();
    }

    private static string ReadRepository(JsonElement settings) =>
        settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("repo", out var repo) && repo.ValueKind == JsonValueKind.String
            ? repo.GetString()!.Trim()
            : "";

    /// <summary>
    /// Lance une actualisation, sauf si la précédente dure encore (réseau lent) : il n'y en a jamais deux en même temps. Appelée par la
    /// minuterie, sur un thread du pool : une exception qui en sortirait arrêterait le processus du module, donc aucune n'en sort. Un rappel
    /// qui arrive pendant la fermeture (<c>_stop</c> déjà libéré) est simplement ignoré.
    /// </summary>
    private bool Refresh()
    {
        try
        {
            if (!_closed && _refresh.IsCompleted)
            {
                Volatile.Write(ref _requestsInThisRefresh, 0);
                _refresh = RefreshAsync(_stop.Token);
                return true;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Services.Log.Warning("Actualisation ignorée : " + ex.Message);
        }

        return false;
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            using var repository = await GetAsync($"repos/{_repository}", ct).ConfigureAwait(false);

            // Les versions, page par page, tant que la page est pleine (il y en a peut-être d'autres) et dans la limite de MaxReleasePages.
            var pages = new List<JsonDocument>();
            try
            {
                var more = true;
                for (var page = 1; more && page <= MaxReleasePages; page++)
                {
                    var releases = await GetAsync($"repos/{_repository}/releases?per_page={ReleasesPerPage}&page={page}", ct).ConfigureAwait(false);
                    pages.Add(releases);
                    more = releases.RootElement is { ValueKind: JsonValueKind.Array } root && root.GetArrayLength() == ReleasesPerPage;
                }

                // Sorti de la boucle avec « more » : la dernière page permise était pleine, le total est donc tronqué.
                _stats = ReadStats(repository.RootElement, pages, truncated: more);
            }
            finally
            {
                pages.ForEach(page => page.Dispose());
            }

            Volatile.Write(ref _requestsPerRefresh, Math.Max(2, Volatile.Read(ref _requestsInThisRefresh)));
            _failedWithoutAnswer = false;
            SetStatus(ModuleHealth.Online);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // La case se ferme.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Réseau coupé, délai dépassé, erreur de GitHub, réponse illisible : la dernière image reste (le logiciel l'assombrit derrière
            // le message), l'état le dit, et la prochaine actualisation (dans 10 minutes) réessaie ; si elle réussit, l'état redevient « en ligne ».
            Services.Log.Warning("Actualisation impossible : " + ex.Message);

            // Une erreur de GitHub (ou une réponse illisible) est une réponse ; le reste (réseau coupé, délai dépassé) n'en est pas une.
            _failedWithoutAnswer = ex is not GitHubProblem;
            SetStatus(ModuleHealth.Offline, ex is GitHubProblem ? ex.Message : "GitHub est injoignable pour le moment.");
        }

        Invalidate();
    }

    /// <summary>Lit les réponses de GitHub ; un JSON qui n'a pas la forme attendue donne un message à part (« illisible »), pas « injoignable ».</summary>
    private static RepoStats ReadStats(JsonElement repository, List<JsonDocument> pages, bool truncated)
    {
        try
        {
            var releases = pages.SelectMany(page => page.RootElement.EnumerateArray());
            return RepoStats.Parse(repository, releases, truncated ? MaxReleasePages * ReleasesPerPage : null);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException or JsonException)
        {
            throw new GitHubProblem("Réponse de GitHub illisible : son format a peut-être changé.", ex);
        }
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.UserAgent.ParseAdd("ashboard-vision-module-github"); // GitHub refuse les requêtes sans User-Agent.
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        // Le jeton est lu dans le coffre à secrets à chaque actualisation (toutes les 10 minutes) : le module ne le garde pas en mémoire.
        var token = Services.Secrets.Get("token");
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new("Bearer", token.Trim());
        }

        // La requête compte dès qu'elle part, qu'elle aboutisse ou non.
        _budget!.Record();
        Interlocked.Increment(ref _requestsInThisRefresh);
        using var response = await _http!.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw GitHubProblem.ForStatus(response.StatusCode, hasToken: !string.IsNullOrWhiteSpace(token));
        }

        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try
        {
            return await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new GitHubProblem("Réponse de GitHub illisible : son format a peut-être changé.", ex);
        }
    }

    /// <summary>Un problème que GitHub a signalé (ou dont il est la cause), avec la phrase à montrer à l'utilisateur.</summary>
    private sealed class GitHubProblem(string message, Exception? inner = null) : Exception(message, inner)
    {
        public static GitHubProblem ForStatus(HttpStatusCode status, bool hasToken) => new(status switch
        {
            HttpStatusCode.NotFound => "Dépôt introuvable : vérifie son nom (propriétaire/nom)" + (hasToken ? " et les droits de ton jeton." : "."),
            HttpStatusCode.Unauthorized => "GitHub refuse ton jeton : vérifie-le dans les réglages du module.",
            // Avec un jeton, un 403 peut être la limite ou un manque de droits : on ne choisit pas à la place de GitHub.
            HttpStatusCode.Forbidden when hasToken => "GitHub refuse la requête : la limite est atteinte, ou ton jeton n'a pas les droits nécessaires.",
            HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests =>
                "GitHub limite les requêtes" + (hasToken ? " : le module réessaiera dans 10 minutes." : " : ajoute un jeton dans les réglages du module."),
            _ => $"GitHub a répondu {(int)status}.",
        });
    }
}

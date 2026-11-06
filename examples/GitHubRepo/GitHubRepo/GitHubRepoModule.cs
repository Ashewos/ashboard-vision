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
    /// <summary>Toutes les 10 minutes : 12 requêtes par heure, loin des 60 que GitHub accorde sans jeton.</summary>
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(10);

    private static readonly Uri ApiRoot = new("https://api.github.com/");

    // propriétaire/nom : les caractères que GitHub accepte, et un nom qui n'est pas fait que de points (« .. » changerait l'adresse demandée).
    private static readonly Regex RepositoryName = new(@"^[A-Za-z0-9-]+/(?!\.+$)[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);

    private readonly CancellationTokenSource _stop = new();
    private string _repository = "";
    private HttpClient? _http;
    private ITimer? _timer;
    private volatile Task _refresh = Task.CompletedTask;
    private volatile RepoStats? _stats;

    /// <summary>Le client HTTP du module. Séparé pour que les tests y glissent de fausses réponses de GitHub.</summary>
    protected virtual HttpClient CreateHttpClient() => Services.CreateHttpClient();

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

        _http = CreateHttpClient();
        _http.BaseAddress = ApiRoot;
        _http.Timeout = TimeSpan.FromSeconds(8);

        Refresh();
        _timer = Services.Time.CreateTimer(_ => Refresh(), null, RefreshEvery, RefreshEvery);
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

        (string Text, float Size, SKColor Color)[] lines =
        [
            (_repository, 16f, ctx.Theme.TextMuted),
            (stats.StarsText, 36f, ctx.Theme.Highlight),
            (stats.ReleaseText(Services.Time.LocalTimeZone), 18f, ctx.Theme.Text),
            (stats.DownloadsText, 18f, ctx.Theme.TextMuted),
        ];

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

    /// <summary>Arrête la minuterie, interrompt l'actualisation en cours et attend sa fin avant de fermer le client HTTP.</summary>
    public override async ValueTask DisposeAsync()
    {
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

    /// <summary>Lance une actualisation, sauf si la précédente dure encore (réseau lent) : il n'y en a jamais deux en même temps.</summary>
    private void Refresh()
    {
        if (_refresh.IsCompleted)
        {
            _refresh = RefreshAsync(_stop.Token);
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            using var repository = await GetAsync($"repos/{_repository}", ct).ConfigureAwait(false);
            using var releases = await GetAsync($"repos/{_repository}/releases?per_page=100", ct).ConfigureAwait(false);
            _stats = RepoStats.Parse(repository.RootElement, releases.RootElement);
            SetStatus(ModuleHealth.Online);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // La case se ferme.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Réseau coupé, délai dépassé, réponse inattendue : la dernière image reste (le logiciel l'assombrit derrière le message), et on réessaiera.
            Services.Log.Warning("Actualisation impossible : " + ex.Message);
            SetStatus(ModuleHealth.Offline, ex is GitHubProblem ? ex.Message : "GitHub est injoignable pour le moment.");
        }

        Invalidate();
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

        using var response = await _http!.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new GitHubProblem(response.StatusCode, !string.IsNullOrWhiteSpace(token));
        }

        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>Une réponse d'erreur de GitHub, avec la phrase à montrer à l'utilisateur.</summary>
    private sealed class GitHubProblem(HttpStatusCode status, bool hasToken) : Exception(Describe(status, hasToken))
    {
        private static string Describe(HttpStatusCode status, bool hasToken) => status switch
        {
            HttpStatusCode.NotFound => "Dépôt introuvable : vérifie son nom (propriétaire/nom)" + (hasToken ? " et les droits de ton jeton." : "."),
            HttpStatusCode.Unauthorized => "GitHub refuse ton jeton : vérifie-le dans les réglages du module.",
            HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests =>
                "GitHub limite les requêtes" + (hasToken ? "." : " : ajoute un jeton dans les réglages du module."),
            _ => $"GitHub a répondu {(int)status}.",
        };
    }
}

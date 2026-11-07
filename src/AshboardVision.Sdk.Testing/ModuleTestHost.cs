using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.ModuleKit;

namespace AshboardVision.Sdk.Testing;

/// <summary>
/// Fait tourner un module de la communauté <b>dans le processus du test</b>, sans enfermement ni service, pour des tests rapides :
/// il lui prête de faux services (réglages, capteurs, musique, coffre à secrets, horloge) et le dessine dans une image avec les vrais
/// thèmes du logiciel. L'essai enfermé, comme le logiciel le lance, est celui du banc d'essai.
/// <para>
/// Deux différences avec le logiciel. Une exception d'une méthode du module sort telle quelle vers le test (le logiciel, lui, met la
/// case en erreur). Et une demande de redessin (<c>Invalidate</c>) ne déclenche rien : c'est le test qui appelle <see cref="Render"/>.
/// </para>
/// <para>Un hôte sert à une instance (une case) et à un seul test ; il se libère avec <c>await using</c>.</para>
/// </summary>
/// <typeparam name="TModule">La classe du module, qui dérive de <see cref="CommunityModule"/> et a un constructeur sans paramètre.</typeparam>
public sealed class ModuleTestHost<TModule> : IAsyncDisposable
    where TModule : CommunityModule, new()
{
    private readonly TestBackend _backend;
    private readonly JsonElement _settings;
    private readonly IReadOnlyList<ModulePermission> _granted;
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "ashboard-module-" + Guid.NewGuid().ToString("N"));
    private TModule? _module;
    private bool _started;
    private bool _disposed;

    /// <summary>
    /// Prépare l'hôte sans démarrer le module.
    /// </summary>
    /// <param name="manifestPath">Le chemin du <c>manifest.json</c> du module : il donne les permissions déclarées et les champs <c>port</c> des réglages.</param>
    /// <param name="settings">Les réglages de la case, tels que l'utilisateur les aurait saisis (un objet JSON) ; absent : un objet vide.</param>
    /// <param name="granted">Les permissions que l'utilisateur accorde, parmi celles que le manifeste déclare ; absent : toutes celles qu'il déclare.</param>
    /// <exception cref="ArgumentException">Une permission accordée n'est pas déclarée dans le manifeste.</exception>
    /// <exception cref="InvalidDataException">Le manifeste n'est pas un JSON valide, n'est pas un objet ou cite une permission inconnue. Les autres champs ne sont pas vérifiés ici : c'est le travail du banc d'essai.</exception>
    /// <exception cref="FileNotFoundException">Le fichier du manifeste n'existe pas.</exception>
    public ModuleTestHost(string manifestPath, JsonElement? settings = null, IReadOnlyList<ModulePermission>? granted = null)
    {
        var manifest = TestManifest.Read(manifestPath);

        _granted = granted is null ? manifest.Permissions : [.. granted.Distinct()];
        foreach (var permission in _granted.Where(permission => !manifest.Permissions.Contains(permission)))
        {
            throw new ArgumentException(
                $"La permission « {ModulePermissions.ToManifest(permission)} » n'est pas déclarée dans le manifeste : le logiciel n'accorde jamais plus que ce que le module déclare.",
                nameof(granted));
        }

        _settings = settings is { ValueKind: JsonValueKind.Object } given ? given.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
        _backend = new TestBackend(manifest.PortFields, _settings);
        Secrets = new FakeSecrets();
        Time = new FakeTimeProvider();
    }

    /// <summary>L'état que le module annonce avec <c>SetStatus</c> ; <see cref="ModuleHealth.Online"/> tant qu'il n'a rien dit.</summary>
    public ModuleHealth Health => _backend.Health;

    /// <summary>Le message de l'état que le module annonce (<c>null</c> s'il n'en a pas donné).</summary>
    public string? StatusMessage => _backend.StatusMessage;

    /// <summary>
    /// Le coffre à secrets du module, en mémoire. Mets-y un jeton avant <see cref="StartAsync"/> pour que le module le trouve, ou lis ce
    /// qu'il y a rangé.
    /// </summary>
    public FakeSecrets Secrets { get; }

    /// <summary>
    /// La source de temps du module (<see cref="ModuleServices.Time"/>) ; le module dessine avec son heure locale (UTC par défaut).
    /// Fais-la avancer (<c>Advance</c>) ou fixe-la (<c>SetUtcNow</c>) pour tester ce qui dépend de l'heure.
    /// </summary>
    public FakeTimeProvider Time { get; }

    /// <summary>
    /// Crée le module, lui donne ses services puis appelle sa méthode <c>StartAsync</c>. Une exception du constructeur ou de
    /// <c>StartAsync</c> du module sort telle quelle.
    /// </summary>
    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_module is not null)
        {
            throw new InvalidOperationException("Le module est déjà créé.");
        }

        Directory.CreateDirectory(_dataDirectory);
        var module = new TModule();
        _module = module;
        module.Bind(new ModuleServices(_settings, new TestLog(), Secrets, Time, _dataDirectory, _granted, _backend));
        await module.StartAsync(CancellationToken.None).ConfigureAwait(false);
        _started = true;
    }

    /// <summary>
    /// Dessine le module dans une image de la taille que sa case aurait sur le damier de l'écran (16 × 4, <see cref="GridGeometry"/>), avec
    /// les écarts du thème, sur un fond transparent : le logiciel pose l'image par-dessus le panneau de la case.
    /// </summary>
    /// <param name="size">La taille de la case en colonnes (1 à 16) et en lignes (1 à 4).</param>
    /// <param name="theme">Le thème : <c>classique</c>, <c>liquid-glass</c> ou <c>pixel</c>.</param>
    /// <returns>L'image (à libérer par l'appelant).</returns>
    /// <exception cref="ArgumentException">Le thème est inconnu.</exception>
    /// <exception cref="ArgumentOutOfRangeException">La taille sort du damier.</exception>
    /// <exception cref="InvalidOperationException"><see cref="StartAsync"/> n'a pas (encore) réussi.</exception>
    public SKBitmap Render(GridSize size, string theme)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(theme);
        var definition = Themes.Find(theme)
            ?? throw new ArgumentException($"Thème inconnu « {theme} » : classique, liquid-glass ou pixel.", nameof(theme));
        if (size.Columns is < 1 or > GridGeometry.Columns)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, $"Le damier a {GridGeometry.Columns} colonnes : une case en a de 1 à {GridGeometry.Columns}.");
        }

        if (size.Rows is < 1 or > GridGeometry.Rows)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, $"Le damier a {GridGeometry.Rows} lignes : une case en a de 1 à {GridGeometry.Rows}.");
        }

        if (!_started || _module is null)
        {
            throw new InvalidOperationException("Appelle StartAsync (et attends-le) avant Render.");
        }

        var rect = GridGeometry.ModuleRect(0, 0, size.Columns, size.Rows, definition.Grid);
        var bitmap = new SKBitmap(new SKImageInfo(rect.Width, rect.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        try
        {
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);
            _module.Render(canvas, new ModuleRenderContext(new SKRect(0, 0, rect.Width, rect.Height), size, definition.Theme, 1f, Time.GetLocalNow()));
            canvas.Flush();
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Pose les capteurs que le module recevra de <see cref="ModuleServices.GetSensorsAsync"/> (s'il a la permission « capteurs ») ;
    /// <c>null</c> : les capteurs ne sont pas disponibles.
    /// </summary>
    public void SetSensors(SensorReading? reading) => _backend.SetSensors(reading);

    /// <summary>
    /// Pose la musique que le module recevra de <see cref="ModuleServices.GetNowPlayingAsync"/> (s'il a la permission « musique ») ;
    /// <c>null</c> : aucune musique en cours.
    /// </summary>
    public void SetNowPlaying(NowPlayingInfo? info) => _backend.SetNowPlaying(info);

    /// <summary>Ferme le module (sa méthode <c>DisposeAsync</c>) puis supprime son dossier de données. Sans effet la seconde fois.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_module is not null)
            {
                _module.CloseSignals();
                await _module.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(_dataDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Un antivirus peut encore tenir un fichier ; le dossier temporaire sera nettoyé plus tard par le système.
            }
        }
    }
}

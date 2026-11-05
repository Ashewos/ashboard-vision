using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AshboardVision.Sdk.Testing;
using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.ModuleKit;

namespace AshboardVision.Sdk.Tests;

/// <summary>Les aides de test des créateurs : un module s'exécute dans le processus du test, avec de faux services et les vrais thèmes.</summary>
public sealed class ModuleTestHostTests : IDisposable
{
    private const string Manifest = """
        {
          "id": "alex.probe", "name": "Sonde", "description": "Un module d'essai.", "author": "Alex", "version": "1.0.0",
          "sdk": "1.0", "entry": "Probe.dll",
          "permissions": ["capteurs", "musique", "ce-pc"],
          "minSize": { "columns": 2, "rows": 1 },
          "settings": [
            { "kind": "text", "key": "titre", "label": "Titre" },
            { "kind": "port", "key": "serveur.port", "label": "Port du serveur", "default": 25565 }
          ]
        }
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ashboard-sdk-testing-tests-" + Guid.NewGuid().ToString("N"));

    public ModuleTestHostTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Render_DrawsAtRequestedSize()
    {
        await using var host = new ModuleTestHost<ProbeModule>(WriteManifest());
        await host.StartAsync();

        // Plein damier avec les écarts de Classique : 1920 − 2 × 14 = 1892 sur 462 − 2 × 14 = 434.
        using var full = host.Render(new GridSize(16, 4), "classique");
        Assert.Equal((1892, 434), (full.Width, full.Height));

        // Une case de 4 × 2 : la taille que le logiciel donnerait à ce module sur le damier, thème par thème.
        foreach (var (name, definition) in new[] { ("classique", Themes.ClassiqueDefinition), ("liquid-glass", Themes.LiquidGlass), ("pixel", Themes.Pixel) })
        {
            var expected = GridGeometry.ModuleRect(0, 0, 4, 2, definition.Grid);
            using var bitmap = host.Render(new GridSize(4, 2), name);
            Assert.Equal((expected.Width, expected.Height), (bitmap.Width, bitmap.Height));
        }

        // Pixel a d'autres écarts que Classique : la taille suit le thème.
        using var classic = host.Render(new GridSize(4, 2), "classique");
        using var pixel = host.Render(new GridSize(4, 2), "pixel");
        Assert.NotEqual((classic.Width, classic.Height), (pixel.Width, pixel.Height));

        // Le module a dessiné avec le thème demandé : un aplat de la couleur d'accent sur toute l'image, coins compris (le canevas partait transparent).
        Assert.Equal(Themes.Pixel.Theme.Accent, pixel.GetPixel(pixel.Width / 2, pixel.Height / 2));
        Assert.Equal(Themes.Pixel.Theme.Accent, pixel.GetPixel(0, 0));
        Assert.Equal(Themes.Pixel.Theme.Accent, pixel.GetPixel(pixel.Width - 1, pixel.Height - 1));
        Assert.Equal(Themes.ClassiqueDefinition.Theme.Accent, classic.GetPixel(classic.Width / 2, classic.Height / 2));
        Assert.Equal(Themes.ClassiqueDefinition.Theme.Accent, classic.GetPixel(0, 0));
        Assert.Equal(Themes.ClassiqueDefinition.Theme.Accent, classic.GetPixel(classic.Width - 1, classic.Height - 1));
        Assert.Equal(new GridSize(4, 2), Probe.LastContext!.Size);
        Assert.Equal(new SKRect(0, 0, pixel.Width, pixel.Height), Probe.LastContext.Bounds);
    }

    [Fact]
    public async Task Render_UnknownThemeOrBadSize_Throws()
    {
        await using var host = new ModuleTestHost<ProbeModule>(WriteManifest());
        await host.StartAsync();

        Assert.Throws<ArgumentException>(() => host.Render(new GridSize(4, 2), "inconnu"));
        Assert.Throws<ArgumentOutOfRangeException>(() => host.Render(new GridSize(17, 2), "classique"));
        Assert.Throws<ArgumentOutOfRangeException>(() => host.Render(new GridSize(4, 0), "classique"));
    }

    [Fact]
    public async Task Render_BeforeStart_Throws()
    {
        await using var host = new ModuleTestHost<ProbeModule>(WriteManifest());

        Assert.Throws<InvalidOperationException>(() => host.Render(new GridSize(4, 2), "classique"));
    }

    [Fact]
    public async Task Render_UsesFakeTimeForNow()
    {
        await using var host = new ModuleTestHost<ProbeModule>(WriteManifest());
        await host.StartAsync();
        host.Time.SetUtcNow(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero));

        using var bitmap = host.Render(new GridSize(4, 2), "classique");

        Assert.Equal(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero), Probe.LastContext!.Now);
        Assert.Same(host.Time, Probe.Given.Time);
    }

    [Fact]
    public async Task DeniedPermission_ReturnsDenied()
    {
        await using var host = new ModuleTestHost<ProbeModule>(WriteManifest(), granted: []);
        host.SetSensors(new SensorReading("CPU", null, new Dictionary<string, double> { ["cpu.temp"] = 61 }));
        host.SetNowPlaying(Song);
        await host.StartAsync();

        var services = Probe.Given;
        Assert.False(services.Has(ModulePermission.Sensors));
        Assert.Equal(DataStatus.Denied, (await services.GetSensorsAsync(CancellationToken.None)).Status);
        Assert.Equal(DataStatus.Denied, (await services.GetNowPlayingAsync(CancellationToken.None)).Status);
        Assert.Equal(DataStatus.Denied, (await services.ConnectLocalAsync(25565, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task GrantedPermissions_DefaultToEveryDeclaredOne()
    {
        await using var host = new ModuleTestHost<ProbeModule>(WriteManifest());
        await host.StartAsync();

        var services = Probe.Given;
        Assert.True(services.Has(ModulePermission.Sensors));
        Assert.True(services.Has(ModulePermission.NowPlaying));
        Assert.True(services.Has(ModulePermission.ThisComputer));
        Assert.False(services.Has(ModulePermission.Internet));
    }

    [Fact]
    public void GrantedPermission_NotDeclaredInManifest_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new ModuleTestHost<ProbeModule>(WriteManifest(), granted: [ModulePermission.Internet]));

        Assert.Contains("internet", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sensors_AreUnavailableUntilSet_ThenOk()
    {
        await using var host = new ModuleTestHost<ProbeModule>(WriteManifest());
        await host.StartAsync();
        var services = Probe.Given;

        Assert.Equal(DataStatus.Unavailable, (await services.GetSensorsAsync(CancellationToken.None)).Status);

        var reading = new SensorReading("CPU", "GPU", new Dictionary<string, double> { ["cpu.temp"] = 61 });
        host.SetSensors(reading);
        var result = await services.GetSensorsAsync(CancellationToken.None);
        Assert.Equal(DataStatus.Ok, result.Status);
        Assert.Same(reading, result.Value);

        host.SetSensors(null);
        Assert.Equal(DataStatus.Unavailable, (await services.GetSensorsAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task NowPlaying_IsUnavailableUntilSet_ThenOk()
    {
        await using var host = new ModuleTestHost<ProbeModule>(WriteManifest());
        await host.StartAsync();
        var services = Probe.Given;

        Assert.Equal(DataStatus.Unavailable, (await services.GetNowPlayingAsync(CancellationToken.None)).Status);

        host.SetNowPlaying(Song);
        var result = await services.GetNowPlayingAsync(CancellationToken.None);
        Assert.Equal(DataStatus.Ok, result.Status);
        Assert.Same(Song, result.Value);

        host.SetNowPlaying(null);
        Assert.Equal(DataStatus.Unavailable, (await services.GetNowPlayingAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Settings_AreGivenToModule()
    {
        using var settings = JsonDocument.Parse("""{ "titre": "Bonjour", "serveur": { "port": 4242 } }""");
        await using var host = new ModuleTestHost<ProbeModule>(WriteManifest(), settings.RootElement);
        await host.StartAsync();

        var given = Probe.Given.Settings;
        Assert.Equal("Bonjour", SettingsDocument.GetString(given, "titre"));
        Assert.Equal(4242, given.GetProperty("serveur").GetProperty("port").GetInt32());
        Assert.True(Probe.Started);
    }

    [Fact]
    public async Task Settings_DefaultToAnEmptyObject()
    {
        await using var host = new ModuleTestHost<ProbeModule>(WriteManifest());
        await host.StartAsync();

        Assert.Equal(JsonValueKind.Object, Probe.Given.Settings.ValueKind);
        Assert.Empty(Probe.Given.Settings.EnumerateObject());
    }

    [Fact]
    public async Task Status_FollowsWhatTheModuleSays()
    {
        await using var host = new ModuleTestHost<ProbeModule>(WriteManifest());
        Assert.Equal(ModuleHealth.Online, host.Health);
        Assert.Null(host.StatusMessage);

        await host.StartAsync();
        Probe.Say(ModuleHealth.NotConfigured, "Renseigne le jeton.");

        Assert.Equal(ModuleHealth.NotConfigured, host.Health);
        Assert.Equal("Renseigne le jeton.", host.StatusMessage);
    }

    [Fact]
    public async Task Secrets_AreSharedWithTheModule()
    {
        await using var host = new ModuleTestHost<ProbeModule>(WriteManifest());
        host.Secrets.Set("jeton", "abc");
        await host.StartAsync();

        Assert.Equal("abc", Probe.Given.Secrets.Get("jeton"));
        Probe.Given.Secrets.Set("autre", "xyz");
        Assert.Equal("xyz", host.Secrets.Get("autre"));
        host.Secrets.Set("jeton", null);
        Assert.Null(Probe.Given.Secrets.Get("jeton"));
    }

    [Fact]
    public async Task ConnectLocal_OnlyReachesThePortsTypedInSettings()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var open = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync();
            await accepted.GetStream().WriteAsync("coucou"u8.ToArray());
        });

        // Le port saisi par l'utilisateur dans le champ « serveur.port » : joignable. Le port proposé par défaut (25565) : plus, il n'est pas saisi.
        using var settings = JsonDocument.Parse($$"""{ "serveur": { "port": {{open}} } }""");
        await using var host = new ModuleTestHost<ProbeModule>(WriteManifest(), settings.RootElement);
        await host.StartAsync();
        var services = Probe.Given;

        Assert.Equal(DataStatus.Denied, (await services.ConnectLocalAsync(25565, CancellationToken.None)).Status);
        Assert.Equal(DataStatus.Denied, (await services.ConnectLocalAsync(open + 1 > 65535 ? 1 : open + 1, CancellationToken.None)).Status);

        var connection = await services.ConnectLocalAsync(open, CancellationToken.None);
        Assert.Equal(DataStatus.Ok, connection.Status);
        await using var stream = connection.Value!;
        var buffer = new byte[6];
        await stream.ReadExactlyAsync(buffer);
        Assert.Equal("coucou", System.Text.Encoding.UTF8.GetString(buffer));
    }

    [Fact]
    public async Task ConnectLocal_WithNothingListening_IsDenied()
    {
        // Un port libre qu'on vient de relâcher : rien n'y écoute. Le relais du logiciel répond « refusé », donc Denied.
        int closed;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            closed = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        using var settings = JsonDocument.Parse($$"""{ "serveur": { "port": {{closed}} } }""");
        await using var host = new ModuleTestHost<ProbeModule>(WriteManifest(), settings.RootElement);
        await host.StartAsync();

        Assert.Equal(DataStatus.Denied, (await Probe.Given.ConnectLocalAsync(closed, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task ConnectLocal_AcceptsTheDefaultPortWhenNothingIsWritten()
    {
        // Comme le service : un réglage non écrit vaut la valeur proposée par le champ.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var manifest = WriteManifest(Manifest.Replace("25565", port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));

        await using var host = new ModuleTestHost<ProbeModule>(manifest);
        await host.StartAsync();
        var connection = await Probe.Given.ConnectLocalAsync(port, CancellationToken.None);

        Assert.Equal(DataStatus.Ok, connection.Status);
        await connection.Value!.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_DisposesTheModuleAndItsDataDirectory()
    {
        var host = new ModuleTestHost<ProbeModule>(WriteManifest());
        await host.StartAsync();
        var module = Probe;
        var directory = module.Given.DataDirectory;
        Assert.True(Directory.Exists(directory));

        await host.DisposeAsync();
        await host.DisposeAsync(); // une seconde fois : sans effet

        Assert.Equal(1, module.DisposeCount);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void Manifest_Unreadable_Throws()
    {
        var bad = Path.Combine(_directory, "mauvais.json");
        File.WriteAllText(bad, """{ "permissions": ["telephone"] }""");

        Assert.Throws<FileNotFoundException>(() => new ModuleTestHost<ProbeModule>(Path.Combine(_directory, "absent.json")));
        Assert.Throws<InvalidDataException>(() => new ModuleTestHost<ProbeModule>(bad));
    }

    [Fact]
    public void Manifest_BrokenJson_ThrowsInvalidData()
    {
        var broken = Path.Combine(_directory, "casse.json");
        File.WriteAllText(broken, """{ "permissions": [ """);
        var notAnObject = Path.Combine(_directory, "tableau.json");
        File.WriteAllText(notAnObject, "[]");

        Assert.Throws<InvalidDataException>(() => new ModuleTestHost<ProbeModule>(broken));
        Assert.Throws<InvalidDataException>(() => new ModuleTestHost<ProbeModule>(notAnObject));
    }

    [Fact]
    public void Secrets_FollowTheServiceLimits()
    {
        var secrets = new FakeSecrets();
        secrets.Set("jeton", "abc");

        // Les limites du superviseur : clé de 1 à 200 caractères, valeur de 65 536 caractères au plus. Hors limites : ignoré, lu comme null.
        secrets.Set("", "vide");
        Assert.Null(secrets.Get(""));
        var longestKey = new string('k', 200);
        secrets.Set(longestKey, "ok");
        Assert.Equal("ok", secrets.Get(longestKey));
        secrets.Set(new string('k', 201), "trop");
        Assert.Null(secrets.Get(new string('k', 201)));

        var biggest = new string('v', 64 * 1024);
        secrets.Set("grand", biggest);
        Assert.Equal(biggest, secrets.Get("grand"));
        secrets.Set("jeton", new string('v', 64 * 1024 + 1)); // trop grande : l'ancienne valeur reste
        Assert.Equal("abc", secrets.Get("jeton"));
        secrets.Set("jeton", null);
        Assert.Null(secrets.Get("jeton"));
    }

    // Le module créé par le dernier StartAsync : les tests d'une même classe s'exécutent l'un après l'autre, et seule cette classe se sert de ProbeModule.
    private static ProbeModule Probe => ProbeModule.Last ?? throw new InvalidOperationException("Aucun module n'a été créé.");

    private static readonly NowPlayingInfo Song = new("Titre", "Artiste", "Lecteur", TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(3), true, null);

    private string WriteManifest(string? text = null)
    {
        var path = Path.Combine(_directory, "manifest-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, text ?? Manifest);
        return path;
    }
}

/// <summary>
/// Un module d'essai qui remplit sa case de la couleur d'accent du thème et garde ce que le logiciel lui a donné.
/// <b>Réservé à cette classe de tests</b> : <c>Last</c> est statique (c'est le seul moyen d'atteindre le module, que l'hôte ne montre pas),
/// donc deux classes de tests qui s'en serviraient en parallèle se marcheraient dessus.
/// </summary>
internal sealed class ProbeModule : CommunityModule
{
    public ProbeModule() => Last = this;

    /// <summary>La dernière instance créée : le test n'a pas d'autre moyen d'atteindre le module que l'hôte fait vivre.</summary>
    public static ProbeModule? Last { get; private set; }

    public ModuleRenderContext? LastContext { get; private set; }

    public bool Started { get; private set; }

    public int DisposeCount { get; private set; }

    public ModuleServices Given => Services;

    public override Task StartAsync(CancellationToken ct)
    {
        Started = true;
        return Task.CompletedTask;
    }

    public override void Render(SKCanvas canvas, ModuleRenderContext ctx)
    {
        LastContext = ctx;
        using var paint = new SKPaint { Color = ctx.Theme.Accent, Style = SKPaintStyle.Fill };
        canvas.DrawRect(ctx.Bounds, paint);
    }

    public override ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }

    public void Say(ModuleHealth health, string message) => SetStatus(health, message);
}

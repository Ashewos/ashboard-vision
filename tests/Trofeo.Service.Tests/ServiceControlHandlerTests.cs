using System.Diagnostics;
using System.Text.Json;
using SkiaSharp;
using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.Core.Shortcuts;
using Trofeo.Device;
using Trofeo.Distribution;
using Trofeo.Extensions;
using Trofeo.Modules.Agents;
using Trofeo.Modules.Clock;
using Trofeo.Modules.NowPlaying;
using Trofeo.ModuleKit;
using Trofeo.TestDoubles;
using static Trofeo.Display.Tests.LoopRig;

namespace Trofeo.Display.Tests;

[Collection(GlobalTraceCollection.Name)]
public sealed partial class ServiceControlHandlerTests
{
    private static readonly Guid Conf1 = new("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid Conf2 = new("cccccccc-0000-0000-0000-000000000002");

    /// <summary>Un type de module configurable : consigne ce qu'on lui demande et peut échouer sur commande.</summary>
    private sealed class ConfigurableProbeFactory : IConfigurableModuleFactory
    {
        public ModuleDescriptor Descriptor { get; } = new(
            "conf", "Configurable", new GridSize(2, 1), new Dictionary<SizePreset, GridSize>(), AcceptsCommands: true);

        public SettingsSchema Schema { get; } = new([new TextField("name", "Nom")]);
        public List<ProbeModule> Created { get; } = [];
        public List<(string ActionKey, JsonElement Settings, ISecretStore Secrets)> Actions { get; } = [];
        public List<(string FieldKey, string Query)> Lookups { get; } = [];
        public ActionResult Result { get; set; } = new(true, "Fait.");

        public IModule Create(ModuleContext context)
        {
            var module = new ProbeModule("conf", context.InstanceId, context.Settings);
            lock (Created) Created.Add(module);
            return module;
        }

        public Task<IReadOnlyList<LookupResult>> LookupAsync(string fieldKey, string query, CancellationToken ct)
        {
            Lookups.Add((fieldKey, query));
            if (query == "boom") throw new InvalidOperationException("réseau coupé");
            return Task.FromResult<IReadOnlyList<LookupResult>>(
                [new LookupResult(query.ToUpperInvariant(), JsonDocument.Parse($"\"{query}\"").RootElement.Clone())]);
        }

        public Task<ActionResult> RunActionAsync(string actionKey, JsonElement settings, ISecretStore secrets, CancellationToken ct)
        {
            Actions.Add((actionKey, settings, secrets));
            if (actionKey == "boom") throw new InvalidOperationException("coffre verrouillé");
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeHotkeyHost : IHotkeyHost
    {
        public List<IReadOnlyList<ShortcutBinding>> Applied { get; } = [];
        public List<string> Failures { get; } = [];

        public IReadOnlyList<string> RefusedKeys { get; set; } = [];

        /// <summary>Le registraire que l'essai d'une touche interroge, à la place de Windows.</summary>
        public FakeRegistrar Registrar { get; } = new();

        /// <summary>Les touches que le service a lui-même enregistrées.</summary>
        public List<string> OwnKeys { get; } = [];

        public Task<ShortcutKeyState> TestAsync(string keys) => Task.FromResult(ShortcutKeyProbe.Test(Registrar, keys, OwnKeys));

        public Task<IReadOnlyList<string>> ApplyAsync(IReadOnlyList<ShortcutBinding> bindings)
        {
            Applied.Add(bindings);
            return Task.FromResult<IReadOnlyList<string>>([.. Failures]);
        }
    }

    /// <summary>Écoute le journal le temps d'un test.</summary>
    private sealed class TraceCapture : IDisposable
    {
        private readonly CapturingTraceListener _listener = new();

        public TraceCapture() => Trace.Listeners.Add(_listener);

        public string[] Lines => _listener.Lines;

        public void Dispose() => Trace.Listeners.Remove(_listener);
    }

    private sealed class NoSystemFonts : ISystemFonts
    {
        public IReadOnlyList<string> Families => [];
        public SKTypeface? Match(string family) => null;
    }

    private sealed class Rig : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-handler-" + Guid.NewGuid().ToString("N"));

        public Rig(
            DashboardLayout? layout = null, Func<ScreenInfo>? screen = null, Func<SensorInfo>? sensors = null,
            Func<DashboardLayout, LoopConfig?, LoopConfig>? build = null, LoopConfig? initialConfig = null, UpdateService? updates = null,
            IReadOnlyList<CommunityEntry>? communityEntries = null, bool withoutHotkeys = false)
        {
            Directory.CreateDirectory(_dir);
            LayoutPath = Path.Combine(_dir, "layout.json");
            Store = new LayoutStore(LayoutPath);
            LoopRig = new LoopRig(
                layout ?? Layout(Place(A, "fake"), Place(Conf1, "conf", column: 4, columns: 2, rows: 1, settings: """{"name":"x"}""")),
                extraFactories: [Factory],
                secrets: type => Secrets.TryGetValue(type, out var s) ? s : Secrets[type] = new FakeSecretStore());
            LoopRig.AddHealthyScreen();
            if (communityEntries is not null) Community.Entries = communityEntries; // ce que le handler voit comme état de départ
            Handler = new ServiceControlHandler(
                Store, LoopRig.Loop, LoopRig.Registry,
                screen ?? (() => new ScreenInfo(LinkScreenState.Ready, null)),
                sensors ?? (() => new SensorInfo(true, false, false)),
                new ProcessMeter(() => (TimeSpan.Zero, 1, 2), LoopRig.Time, 4),
                build ?? ((layout, current) => LoopConfigFactory.Build(layout, new NoSystemFonts(), null, Sink.Warn, current)),
                withoutHotkeys ? null : Hotkeys, Community, Sink, initialConfig, updates);
        }

        public string LayoutPath { get; }
        public LayoutStore Store { get; }
        public Dictionary<string, FakeSecretStore> Secrets { get; } = [];
        public ConfigurableProbeFactory Factory { get; } = new();
        public LoopRig LoopRig { get; }
        public FakeHotkeyHost Hotkeys { get; } = new();
        public FakeCommunityModules Community { get; } = new();
        public WarningSink Sink { get; } = new();
        public ServiceControlHandler Handler { get; }

        /// <summary>Fait tourner la boucle jusqu'à ce que la tâche soit finie : les changements se font au début d'une itération.</summary>
        public async Task<T> Pump<T>(Task<T> task)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!task.IsCompleted)
            {
                Assert.True(DateTime.UtcNow < deadline, "La boucle n'a pas traité la demande.");
                LoopRig.Loop.Tick();
                await Task.Delay(1);
            }

            return await task;
        }

        public void Dispose()
        {
            Handler.Dispose();
            LoopRig.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }
    }

    // ---- Rechargement ----

    [Fact]
    public async Task Reload_ValidFile_AppliesAndReturnsWarnings()
    {
        using var rig = new Rig();
        var next = Layout(Place(B, "other", column: 6)) with { MaxFps = 5 };
        rig.Store.Save(next);
        File.WriteAllText(rig.LayoutPath, File.ReadAllText(rig.LayoutPath).Replace("\"MaxFps\": 5", "\"MaxFps\": 40"));

        var result = await rig.Pump(rig.Handler.ReloadAsync(CancellationToken.None));

        Assert.True(result.Applied);
        Assert.Null(result.Error);
        Assert.Equal(["Plafond d'images invalide : 12 images/s utilisées."], result.Warnings);
        var placement = Assert.Single(rig.LoopRig.Loop.Layout.Views[0].Placements);
        Assert.Equal(B, placement.InstanceId);
        Assert.Single(rig.Hotkeys.Applied);
    }

    [Fact]
    public async Task Reload_WarningsComeInOrder_FileAppearanceLoopHotkeys()
    {
        using var rig = new Rig();
        var next = Layout(Place(A, "ghost"), Place(B, "other", column: 6)) with
        {
            MaxFps = 5,
            Appearance = new AppearanceSettings(Font: "PoliceIntrouvable"),
        };
        rig.Store.Save(next);
        File.WriteAllText(rig.LayoutPath, File.ReadAllText(rig.LayoutPath).Replace("\"MaxFps\": 5", "\"MaxFps\": 40"));
        rig.Hotkeys.Failures.Add("Raccourci « Ctrl+X » indisponible.");

        var result = await rig.Pump(rig.Handler.ReloadAsync(CancellationToken.None));

        Assert.True(result.Applied);
        Assert.Equal(4, result.Warnings.Count);
        Assert.Contains("Plafond d'images", result.Warnings[0]);
        Assert.Contains("PoliceIntrouvable", result.Warnings[1]);
        Assert.Contains("ghost", result.Warnings[2]);
        Assert.Equal("Raccourci « Ctrl+X » indisponible.", result.Warnings[3]);
    }

    [Fact]
    public async Task Reload_InvalidFile_KeepsCurrentAndReportsReason()
    {
        using var rig = new Rig();
        var before = rig.LoopRig.Loop.Layout;
        rig.Store.Save(Layout(Place(A, "fake"), Place(B, "other")));  // les deux au même endroit

        var result = await rig.Pump(rig.Handler.ReloadAsync(CancellationToken.None));

        Assert.False(result.Applied);
        Assert.Equal("Deux modules se chevauchent : fake et other.", result.Error);
        Assert.Empty(result.Warnings);
        Assert.Same(before, rig.LoopRig.Loop.Layout);
        Assert.False(File.Exists(rig.LayoutPath + ".bad"));
        Assert.Empty(rig.Hotkeys.Applied);
    }

    [Fact]
    public async Task Reload_MissingBackground_ReturnsWarning()
    {
        using var rig = new Rig();
        var missing = Path.Combine(Path.GetTempPath(), "trofeo-absent", "fond.png");
        rig.Store.Save(Layout(Place(A, "fake")) with
        {
            Appearance = new AppearanceSettings(Background: new BackgroundSettings(BackgroundKind.Image, Path: missing)),
        });

        var result = await rig.Pump(rig.Handler.ReloadAsync(CancellationToken.None));

        Assert.True(result.Applied);
        Assert.Contains(result.Warnings, w => w.Contains("fond.png"));
    }

    [Fact]
    public async Task Reload_NoWarningLeaksFromOneReloadToTheNext()
    {
        using var rig = new Rig();
        rig.Sink.Warn("reste d'un démarrage");
        rig.Store.Save(Layout(Place(A, "fake")));

        var first = await rig.Pump(rig.Handler.ReloadAsync(CancellationToken.None));
        var second = await rig.Pump(rig.Handler.ReloadAsync(CancellationToken.None));

        Assert.Empty(first.Warnings);
        Assert.Empty(second.Warnings);
    }

    [Fact]
    public async Task Reload_SameAppearance_KeepsBackgroundInstance()
    {
        var built = new List<TrackedBackground>();
        using var rig = new Rig(build: (layout, _) =>
        {
            var background = new TrackedBackground(SKColors.Red);
            built.Add(background);
            return new LoopConfig(layout, Themes.ClassiqueDefinition, background);
        });
        var appearance = new AppearanceSettings("pixel");
        rig.Store.Save(Layout(Place(A, "fake")) with { Appearance = appearance });
        await rig.Pump(rig.Handler.ReloadAsync(CancellationToken.None));

        rig.Store.Save(Layout(Place(A, "fake", column: 6)) with { Appearance = appearance with { } }); // un module déplacé
        var result = await rig.Pump(rig.Handler.ReloadAsync(CancellationToken.None));

        Assert.True(result.Applied);
        Assert.Single(built);
        Assert.False(built[0].Disposed);
        Assert.Equal(6, rig.LoopRig.Loop.Layout.Views[0].Placements[0].Column);
    }

    [Fact]
    public async Task Reload_SameAppearanceAsStartup_DoesNotRebuild()
    {
        var builds = 0;
        var startup = new LoopConfig(Layout(Place(A, "fake")), Themes.ClassiqueDefinition, new TrackedBackground(SKColors.Red));
        using var rig = new Rig(build: (layout, _) =>
        {
            builds++;
            return new LoopConfig(layout, Themes.ClassiqueDefinition, new TrackedBackground(SKColors.Blue));
        }, initialConfig: startup);
        rig.Store.Save(Layout(Place(A, "fake", column: 6)));  // apparence absente, comme au départ

        await rig.Pump(rig.Handler.ReloadAsync(CancellationToken.None));

        Assert.Equal(0, builds);
    }

    [Fact]
    public async Task Reload_ChangedAppearance_RebuildsBackground()
    {
        var built = new List<TrackedBackground>();
        using var rig = new Rig(build: (layout, _) =>
        {
            var background = new TrackedBackground(SKColors.Red);
            built.Add(background);
            return new LoopConfig(layout, Themes.ClassiqueDefinition, background);
        });
        rig.Store.Save(Layout(Place(A, "fake")) with { Appearance = new AppearanceSettings("pixel") });
        await rig.Pump(rig.Handler.ReloadAsync(CancellationToken.None));

        rig.Store.Save(Layout(Place(A, "fake")) with { Appearance = new AppearanceSettings("pixel", PanelOpacity: 0.5f) });
        await rig.Pump(rig.Handler.ReloadAsync(CancellationToken.None));

        Assert.Equal(2, built.Count);
        Assert.True(built[0].Disposed);   // la boucle a libéré l'ancien fond
        Assert.False(built[1].Disposed);
    }

    [Fact]
    public async Task Reload_SameAppearance_KeepsBackgroundWarningsForTheNextReload()
    {
        using var rig = new Rig();
        var missing = Path.Combine(Path.GetTempPath(), "trofeo-absent", "fond.png");
        var background = new BackgroundSettings(BackgroundKind.Image, Path: missing);
        rig.Store.Save(Layout(Place(A, "fake")) with { Appearance = new AppearanceSettings(Background: background) });
        var first = await rig.Pump(rig.Handler.ReloadAsync(CancellationToken.None));

        rig.Store.Save(Layout(Place(A, "fake", column: 6)) with { Appearance = new AppearanceSettings(Background: background) }); // un module déplacé
        await rig.Pump(rig.Handler.ReloadAsync(CancellationToken.None));
        rig.Store.Save(Layout(Place(A, "fake", column: 6)) with
        {
            Appearance = new AppearanceSettings(PanelOpacity: 0.5f, Background: background), // seule l'opacité change
        });
        var third = await rig.Pump(rig.Handler.ReloadAsync(CancellationToken.None));

        Assert.Contains(first.Warnings, w => w.Contains("fond.png"));
        Assert.Contains(third.Warnings, w => w.Contains("fond.png")); // le fond de repli est toujours à l'écran : il reste signalé
    }

    [Fact]
    public async Task Reload_PassesShortcutsToHotkeyHost()
    {
        using var rig = new Rig();
        ShortcutBinding[] custom = [new("Ctrl+Alt+Up", ShortcutCommand.NextTab)];
        rig.Store.Save(Layout(Place(A, "fake")) with { Shortcuts = custom });
        await rig.Pump(rig.Handler.ReloadAsync(CancellationToken.None));

        rig.Store.Save(Layout(Place(A, "fake")));
        await rig.Pump(rig.Handler.ReloadAsync(CancellationToken.None));

        Assert.Equal(2, rig.Hotkeys.Applied.Count);
        Assert.Equal(custom, rig.Hotkeys.Applied[0]);
        Assert.Same(ShortcutDefaults.All, rig.Hotkeys.Applied[1]);
    }

    [Fact]
    public async Task Reload_PassesTheAppliedConfigToBuild()
    {
        var seen = new List<LoopConfig?>();
        using var rig = new Rig(build: (layout, current) =>
        {
            seen.Add(current);
            return new LoopConfig(layout, Themes.ClassiqueDefinition, new TrackedBackground(SKColors.Red));
        });
        rig.Store.Save(Layout(Place(A, "fake")) with { Appearance = new AppearanceSettings("pixel") });
        await rig.Pump(rig.Handler.ReloadAsync(CancellationToken.None));
        rig.Store.Save(Layout(Place(A, "fake")) with { Appearance = new AppearanceSettings("pixel", PanelOpacity: 0.5f) });

        await rig.Pump(rig.Handler.ReloadAsync(CancellationToken.None));

        Assert.Equal(2, seen.Count);
        Assert.Null(seen[0]);
        Assert.NotNull(seen[1]);
        Assert.Equal(new AppearanceSettings("pixel"), seen[1]!.Layout.Appearance);
    }

    [Fact]
    public async Task Reload_OpacityChange_KeepsTheVideoOpen()
    {
        var opened = 0;
        var videos = new List<TrackedBackground>();
        using var rig = new Rig(build: (layout, current) => LoopConfigFactory.Build(layout, new NoSystemFonts(), _ =>
        {
            opened++;
            var video = new TrackedBackground(SKColors.Red);
            videos.Add(video);
            return video;
        }, _ => { }, current));
        var video = new BackgroundSettings(BackgroundKind.Video, Path: "clip.mp4");
        rig.Store.Save(Layout(Place(A, "fake")) with { Appearance = new AppearanceSettings("classique", Background: video) });
        await rig.Pump(rig.Handler.ReloadAsync(CancellationToken.None));

        rig.Store.Save(Layout(Place(A, "fake")) with
        {
            Appearance = new AppearanceSettings("classique", PanelOpacity: 0.5f, Font: "Inconnue", Legibility: false, Background: video),
        });
        var result = await rig.Pump(rig.Handler.ReloadAsync(CancellationToken.None));

        Assert.True(result.Applied);
        Assert.Equal(1, opened);
        Assert.False(videos[0].Disposed);
        Assert.Equal(0.5f, rig.LoopRig.Loop.Layout.Appearance!.PanelOpacity);
    }

    [Fact]
    public async Task Reload_UnchangedShortcutsAndCommandableModule_DoNotReapplyHotkeys()
    {
        using var rig = new Rig();
        rig.Hotkeys.Failures.Add("Raccourci « Ctrl+Alt+Left » indisponible : déjà pris.");
        rig.Store.Save(Layout(Place(A, "fake"), Place(Conf1, "conf", column: 4, columns: 2, rows: 1)));
        var first = await rig.Pump(rig.Handler.ReloadAsync(CancellationToken.None));

        rig.Store.Save(Layout(Place(A, "fake"), Place(Conf1, "conf", column: 6, columns: 2, rows: 1))); // un module déplacé
        var second = await rig.Pump(rig.Handler.ReloadAsync(CancellationToken.None));

        Assert.True(second.Applied);
        Assert.Single(rig.Hotkeys.Applied);
        Assert.Contains(first.Warnings, w => w.Contains("Ctrl+Alt+Left"));
        Assert.DoesNotContain(second.Warnings, w => w.Contains("Ctrl+Alt+Left")); // le refus de la pose précédente n'est pas répété
    }

    [Fact]
    public async Task Reload_ShortcutsChanged_ReappliesHotkeys()
    {
        using var rig = new Rig();
        rig.Store.Save(Layout(Place(A, "fake"), Place(Conf1, "conf", column: 4, columns: 2, rows: 1)));
        await rig.Pump(rig.Handler.ReloadAsync(CancellationToken.None));
        ShortcutBinding[] custom = [new("Ctrl+Alt+Up", ShortcutCommand.NextTab)];

        rig.Store.Save(Layout(Place(A, "fake"), Place(Conf1, "conf", column: 4, columns: 2, rows: 1)) with { Shortcuts = custom });
        await rig.Pump(rig.Handler.ReloadAsync(CancellationToken.None));

        Assert.Equal(2, rig.Hotkeys.Applied.Count);
        Assert.Equal(custom, rig.Hotkeys.Applied[1]);
    }

    [Fact]
    public async Task Reload_CommandableModuleAppearsOrDisappears_DoesNotReapplyHotkeys()
    {
        using var rig = new Rig();
        rig.Store.Save(Layout(Place(A, "fake"), Place(Conf1, "conf", column: 4, columns: 2, rows: 1)));
        await rig.Pump(rig.Handler.ReloadAsync(CancellationToken.None));

        rig.Store.Save(Layout()); // plus de module commandable
        await rig.Pump(rig.Handler.ReloadAsync(CancellationToken.None));
        rig.Store.Save(Layout(Place(A, "fake"), Place(Conf1, "conf", column: 4, columns: 2, rows: 1))); // il revient
        await rig.Pump(rig.Handler.ReloadAsync(CancellationToken.None));

        Assert.Single(rig.Hotkeys.Applied); // les raccourcis ne dépendent plus d'un module commandable : rien à reposer
        Assert.Same(ShortcutDefaults.All, rig.Hotkeys.Applied[0]);
    }

    [Fact]
    public async Task Reload_BuildConfigFails_ReportsErrorAndKeepsCurrent()
    {
        using var rig = new Rig();
        var failing = new ServiceControlHandler(
            rig.Store, rig.LoopRig.Loop, rig.LoopRig.Registry, () => new ScreenInfo(LinkScreenState.Ready, null),
            () => new SensorInfo(false, false, false), new ProcessMeter(() => (TimeSpan.Zero, 0, 0), rig.LoopRig.Time, 1),
            (_, _) => throw new InvalidOperationException("police corrompue"), rig.Hotkeys, rig.Community);
        var before = rig.LoopRig.Loop.Layout;
        rig.Store.Save(Layout(Place(B, "other")));

        var result = await rig.Pump(failing.ReloadAsync(CancellationToken.None));

        Assert.False(result.Applied);
        Assert.Equal("L'apparence n'a pas pu être appliquée : police corrompue", result.Error);
        Assert.Same(before, rig.LoopRig.Loop.Layout);
    }

    // ---- Recherche ----

    [Fact]
    public async Task Lookup_UsesConfigurableFactory()
    {
        using var rig = new Rig();

        var results = await rig.Handler.LookupAsync("conf", "location", "paris", CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal("PARIS", result.Label);
        Assert.Equal([("location", "paris")], rig.Factory.Lookups);
    }

    [Fact]
    public async Task Lookup_UnknownOrPlainFactory_IsEmpty()
    {
        using var rig = new Rig();

        Assert.Empty(await rig.Handler.LookupAsync("fake", "x", "q", CancellationToken.None));  // fabrique non configurable
        Assert.Empty(await rig.Handler.LookupAsync("nope", "x", "q", CancellationToken.None));  // type inconnu
    }

    [Fact]
    public async Task Lookup_FactoryThrows_ReturnsEmptyAndLogsWarning()
    {
        using var rig = new Rig();
        using var trace = new TraceCapture();

        var results = await rig.Handler.LookupAsync("conf", "location", "boom", CancellationToken.None);

        Assert.Empty(results);
        Assert.Contains(trace.Lines, l => l.Contains("réseau coupé"));
    }

    // ---- Actions ----

    [Fact]
    public async Task Action_PassesSettingsAndTypeSecrets()
    {
        using var rig = new Rig();

        var result = await rig.Pump(rig.Handler.RunActionAsync(Conf1, "ok", CancellationToken.None));

        Assert.Equal(new ActionResult(true, "Fait."), result);
        var call = Assert.Single(rig.Factory.Actions);
        Assert.Equal("ok", call.ActionKey);
        Assert.Equal("""{"name":"x"}""", call.Settings.GetRawText());
        Assert.Same(rig.Secrets["conf"], call.Secrets);
    }

    [Fact]
    public async Task Action_FindsShelvedInstanceToo()
    {
        var layout = Layout(Place(A, "fake")) with
        {
            Shelf = [Place(Conf2, "conf", settings: """{"name":"rangé"}""")],
        };
        using var rig = new Rig(layout);

        var result = await rig.Pump(rig.Handler.RunActionAsync(Conf2, "ok", CancellationToken.None));

        Assert.True(result.Ok);
        Assert.Equal("""{"name":"rangé"}""", Assert.Single(rig.Factory.Actions).Settings.GetRawText());
        Assert.Empty(rig.Factory.Created);  // rien à redémarrer : le module n'est pas à l'écran
    }

    [Fact]
    public async Task Action_UnknownInstance_Refuses()
    {
        using var rig = new Rig();

        var result = await rig.Pump(rig.Handler.RunActionAsync(Guid.NewGuid(), "ok", CancellationToken.None));

        Assert.Equal(new ActionResult(false, "Module introuvable dans la disposition."), result);
        Assert.Empty(rig.Factory.Actions);
    }

    [Fact]
    public async Task Action_Success_RestartsPlacedInstance()
    {
        using var rig = new Rig();
        var first = Assert.Single(rig.Factory.Created);

        var result = await rig.Pump(rig.Handler.RunActionAsync(Conf1, "ok", CancellationToken.None));

        Assert.True(result.Ok);
        Assert.Equal(2, rig.Factory.Created.Count);
        Assert.Equal(1, first.Disposed);
    }

    [Fact]
    public async Task Action_Failure_DoesNotRestart()
    {
        using var rig = new Rig();
        rig.Factory.Result = new ActionResult(false, "Refusé.");

        var result = await rig.Pump(rig.Handler.RunActionAsync(Conf1, "ok", CancellationToken.None));

        Assert.Equal(new ActionResult(false, "Refusé."), result);
        Assert.Single(rig.Factory.Created);
    }

    [Fact]
    public async Task Action_FactoryThrows_ReturnsFailureAndLogsWarning()
    {
        using var rig = new Rig();
        using var trace = new TraceCapture();

        var result = await rig.Pump(rig.Handler.RunActionAsync(Conf1, "boom", CancellationToken.None));

        Assert.Equal(new ActionResult(false, "L'action a échoué : coffre verrouillé"), result);
        Assert.Single(rig.Factory.Created);  // pas de redémarrage
        Assert.Contains(trace.Lines, l => l.Contains("coffre verrouillé"));
    }

    [Fact]
    public async Task Action_PlainFactory_IsUnknownAction()
    {
        using var rig = new Rig();

        var result = await rig.Pump(rig.Handler.RunActionAsync(A, "ok", CancellationToken.None));

        Assert.Equal(new ActionResult(false, "Action inconnue."), result);
    }

    [Fact]
    public async Task RestartModule_RecreatesPlacedInstanceOnly()
    {
        using var rig = new Rig();

        Assert.True(await rig.Pump(rig.Handler.RestartModuleAsync(Conf1, CancellationToken.None)));
        Assert.False(await rig.Pump(rig.Handler.RestartModuleAsync(Guid.NewGuid(), CancellationToken.None)));
        Assert.Equal(2, rig.Factory.Created.Count);
    }

    // ---- État ----

    [Fact]
    public void Hello_ListsRegistryTypesWithSchemas()
    {
        using var http = new HttpClient();
        var registry = new ModuleRegistry(
            [
                new ClockFactory(new OpenMeteoClient(http)),
                new NowPlayingFactory(_ => throw new InvalidOperationException("jamais créé")),
                CodexFactory.CreateProduction(),
            ],
            _ => new FakeSecretStore());
        using var rig = new Rig();
        var handler = new ServiceControlHandler(
            rig.Store, rig.LoopRig.Loop, registry, () => new ScreenInfo(LinkScreenState.Ready, null),
            () => new SensorInfo(false, false, false), new ProcessMeter(() => (TimeSpan.Zero, 0, 0), rig.LoopRig.Time, 1),
            (_, _) => throw new InvalidOperationException(), null, rig.Community);

        var welcome = handler.Hello();

        Assert.Equal(ControlPipe.ProtocolVersion, welcome.ProtocolVersion);
        Assert.False(string.IsNullOrEmpty(welcome.ServiceVersion));
        Assert.Equal(["clock", "nowplaying", "codex"], welcome.Types.Select(t => t.TypeId));
        var clock = welcome.Types[0];
        Assert.Equal("Heure, date et météo", clock.DisplayName);
        Assert.False(clock.AcceptsCommands);
        Assert.NotEmpty(clock.Schema.Fields);
        Assert.Equal(new GridSize(3, 1), clock.MinSize);
        Assert.Equal(new GridSize(4, 2), clock.Presets[SizePreset.Ideal]);
        var nowPlaying = welcome.Types[1];
        Assert.Same(SettingsSchema.Empty, nowPlaying.Schema);
        Assert.False(nowPlaying.AcceptsCommands);
        Assert.True(welcome.Types[2].AcceptsCommands);
    }

    [Fact]
    public void GetStatus_ReportsScreenModulesProcessFramesAndSensors()
    {
        using var rig = new Rig(
            screen: () => new ScreenInfo(LinkScreenState.Recovering, "reprise"),
            sensors: () => new SensorInfo(true, true, false));
        rig.LoopRig.Loop.Start(CancellationToken.None);
        var probe = Assert.Single(rig.Factory.Created);
        probe.Status = new ModuleStatus(ModuleHealth.Offline, "Pas de réseau");
        probe.Notices = ["Une remarque"];

        var status = rig.Handler.GetStatus();

        Assert.Equal(new ScreenInfo(LinkScreenState.Recovering, "reprise"), status.Screen);
        Assert.False(status.Emulated);
        Assert.Equal(new SensorInfo(true, true, false), status.Sensors);
        Assert.Equal(new ProcessInfo(1, 2, 0), status.Process);
        Assert.Equal(new FrameInfo(0, 0, 0), status.Frames);
        Assert.Equal(2, status.Modules.Count);
        var conf = Assert.Single(status.Modules, m => m.InstanceId == Conf1);
        Assert.Equal("conf", conf.TypeId);
        Assert.Equal(ModuleHealth.Offline, conf.Health);
        Assert.Equal("Pas de réseau", conf.ActionMessage);
        Assert.Equal(["Une remarque"], conf.Notices);
    }

    [Theory]
    [InlineData(ScreenState.Ready, null, LinkScreenState.Ready)]
    [InlineData(ScreenState.Recovering, null, LinkScreenState.Recovering)]
    [InlineData(ScreenState.Disconnected, "Écran non branché", LinkScreenState.Disconnected)]
    public void ToScreenInfo_MapsDeviceState(ScreenState state, string? reason, LinkScreenState expected)
    {
        Assert.Equal(new ScreenInfo(expected, reason), ServiceControlHandler.ToScreenInfo(new ScreenStatus(state, reason)));
    }

    [Fact]
    public void SubscribePreview_DelegatesToTheLoop()
    {
        using var rig = new Rig();
        var frames = new List<PreviewFrame>();

        using var subscription = rig.Handler.SubscribePreview(10, null, frames.Add);
        rig.LoopRig.Loop.Tick();
        rig.LoopRig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.LoopRig.Loop.Tick();

        Assert.NotEmpty(frames);
    }

    // ---- Mises à jour ----

    /// <summary>Un vrai <see cref="UpdateService"/> sur un faux GitHub : la version 1.0.0 est publiée, et son installateur est introuvable.</summary>
    private sealed class UpdateRig : HttpMessageHandler
    {
        private const string Feed = """
            [{"tag_name":"v1.0.0","draft":false,"prerelease":false,"published_at":"2026-10-29T10:00:00Z","assets":[
              {"name":"Ashboard-Vision-Setup-1.0.0.exe","size":5000,"browser_download_url":"https://github.com/Ashewos/ashboard-vision/releases/download/v1.0.0/Ashboard-Vision-Setup-1.0.0.exe"},
              {"name":"Ashboard-Vision-Setup-1.0.0.exe.sha256","size":80,"browser_download_url":"https://github.com/Ashewos/ashboard-vision/releases/download/v1.0.0/Ashboard-Vision-Setup-1.0.0.exe.sha256"}]}]
            """;

        private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-maj-" + Guid.NewGuid().ToString("N"));

        public UpdateRig(string installed = "1.1.0-beta.1")
        {
            var http = new HttpClient(this);
            // Le scénario est une bêta installée qui suit la stable : ce choix est enregistré, sinon une bêta suivrait le canal bêta.
            var store = new UpdatePreferencesStore(Path.Combine(_dir, "mises-a-jour.json"));
            store.Save(UpdatePreferences.Default);
            Service = new UpdateService(
                new UpdateChecker(http), new UpdateDownloader(http), new RefusingLauncher(), store,
                AppVersion.TryParse(installed, out var version) ? version : throw new ArgumentException(installed),
                Path.Combine(_dir, "reglages"), Path.Combine(_dir, "sauvegardes"), Path.Combine(_dir, "telechargement"), TimeProvider.System);
        }

        public UpdateService Service { get; }
        public List<string> Requests { get; } = [];

        /// <summary>Si renseignée, la demande de l'empreinte attend qu'on la libère (le téléchargement est « en cours »).</summary>
        public TaskCompletionSource? HoldDownload { get; set; }

        /// <summary>Résolue quand la demande de l'empreinte est arrivée ; rend le jeton d'annulation qu'elle a reçu.</summary>
        public TaskCompletionSource<CancellationToken> DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            lock (Requests) Requests.Add(url);
            if (HoldDownload is { } hold && url.EndsWith(".sha256", StringComparison.Ordinal))
            {
                DownloadStarted.TrySetResult(cancellationToken);
                await hold.Task.WaitAsync(cancellationToken);
            }

            var response = url == ReleaseFeed.Url
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(Feed) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            return response;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Service.Dispose();
                try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
            }

            base.Dispose(disposing);
        }

        private sealed class RefusingLauncher : IInstallerLauncher
        {
            public InstallerLaunch Launch(string setupPath) => throw new InvalidOperationException("Rien ne doit être lancé.");
        }
    }

    [Fact]
    public void GetUpdate_WithoutUpdateService_ReturnsUnknown()
    {
        using var rig = new Rig();

        var update = rig.Handler.GetUpdate();

        Assert.Equal(new UpdateInfo(LinkUpdateState.Unknown, null, false, null, null, LinkUpdateChannel.Stable, false), update);
    }

    [Fact]
    public async Task UpdateRequests_WithoutUpdateService_DoNothingAndRefuseToInstall()
    {
        using var rig = new Rig();

        await rig.Handler.CheckUpdatesAsync(CancellationToken.None);
        await rig.Handler.SetUpdatePreferencesAsync(LinkUpdateChannel.Beta, true, CancellationToken.None);
        var install = await rig.Handler.InstallUpdateAsync(true, CancellationToken.None);

        Assert.False(install.Ok);
        Assert.Equal("Les mises à jour ne sont pas disponibles pour l'instant.", install.Message);
    }

    [Fact]
    public async Task GetUpdate_AfterCheck_MapsStateVersionAndPreferences()
    {
        using var updates = new UpdateRig();
        using var rig = new Rig(updates: updates.Service);

        await rig.Handler.CheckUpdatesAsync(CancellationToken.None);
        var update = rig.Handler.GetUpdate();

        Assert.Equal(
            new UpdateInfo(LinkUpdateState.Available, "1.0.0", true, null, "La version stable 1.0.0 est disponible (retour depuis la bêta).", LinkUpdateChannel.Stable, false),
            update);
    }

    [Theory]
    [MemberData(nameof(AllUpdateStates))]
    public void ToUpdateInfo_EveryStateHasItsCounterpart(UpdateState state)
    {
        var info = ServiceControlHandler.ToUpdateInfo(
            new UpdateStatus(state, "1.0.1", false, 7, "Message", new UpdatePreferences(UpdateChannel.Beta, true)));

        Assert.Equal(state.ToString(), info.State.ToString());
        Assert.Equal(new UpdateInfo(info.State, "1.0.1", false, 7, "Message", LinkUpdateChannel.Beta, true), info);
    }

    public static IEnumerable<object[]> AllUpdateStates() => Enum.GetValues<UpdateState>().Select(s => new object[] { s });

    [Fact]
    public async Task SetUpdatePreferences_SavesTheChoicesAndChecksAgain()
    {
        using var updates = new UpdateRig();
        using var rig = new Rig(updates: updates.Service);

        await rig.Handler.SetUpdatePreferencesAsync(LinkUpdateChannel.Beta, waitForNextStable: true, CancellationToken.None);

        Assert.Equal(new UpdatePreferences(UpdateChannel.Beta, true), updates.Service.Current.Preferences);
        var info = rig.Handler.GetUpdate();
        Assert.Equal(LinkUpdateChannel.Beta, info.Channel);
        Assert.True(info.WaitForNextStable);
        Assert.Contains(ReleaseFeed.Url, updates.Requests);
    }

    [Fact]
    public async Task InstallUpdate_ForwardsConfirmDowngrade()
    {
        using var updates = new UpdateRig();
        using var rig = new Rig(updates: updates.Service);
        await rig.Handler.CheckUpdatesAsync(CancellationToken.None);

        // Retour en arrière sans confirmation : refusé avant tout téléchargement.
        var refused = await rig.Handler.InstallUpdateAsync(confirmDowngrade: false, CancellationToken.None);
        Assert.False(refused.Ok);
        Assert.Equal("Revenir à une version plus ancienne demande ta confirmation.", refused.Message);
        Assert.DoesNotContain(updates.Requests, u => u.EndsWith(".sha256", StringComparison.Ordinal));

        // Confirmé : le téléchargement commence (le faux GitHub ne trouve pas l'installateur, et rien n'est lancé).
        var confirmed = await rig.Handler.InstallUpdateAsync(confirmDowngrade: true, CancellationToken.None);
        Assert.False(confirmed.Ok);
        Assert.StartsWith("Le téléchargement a échoué", confirmed.Message);
        Assert.Contains(updates.Requests, u => u.EndsWith(".sha256", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallUpdate_RequestCancelled_DoesNotCancelTheInstallation()
    {
        using var updates = new UpdateRig();
        using var rig = new Rig(updates: updates.Service);
        await rig.Handler.CheckUpdatesAsync(CancellationToken.None);
        updates.HoldDownload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var request = new CancellationTokenSource();

        var install = rig.Handler.InstallUpdateAsync(confirmDowngrade: true, request.Token);
        var downloadToken = await updates.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        request.Cancel(); // la fenêtre de réglage se ferme pendant le téléchargement

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install);
        Assert.False(downloadToken.IsCancellationRequested);

        // Le téléchargement continue : libéré, il va jusqu'au bout (ici l'installateur est introuvable, donc « échec », pas « annulé »).
        updates.HoldDownload.SetResult();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (updates.Service.Current.State is UpdateState.Downloading or UpdateState.Available)
        {
            Assert.True(DateTime.UtcNow < deadline, "L'installation ne s'est pas terminée.");
            await Task.Delay(10);
        }

        Assert.Equal(UpdateState.Failed, updates.Service.Current.State);
    }

    [Fact]
    public async Task InstallUpdate_ServiceStops_CancelsTheInstallation()
    {
        using var updates = new UpdateRig();
        using var rig = new Rig(updates: updates.Service);
        await rig.Handler.CheckUpdatesAsync(CancellationToken.None);
        updates.HoldDownload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var install = rig.Handler.InstallUpdateAsync(confirmDowngrade: true, CancellationToken.None);
        var downloadToken = await updates.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Handler.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install);
        Assert.True(downloadToken.IsCancellationRequested);
    }
}

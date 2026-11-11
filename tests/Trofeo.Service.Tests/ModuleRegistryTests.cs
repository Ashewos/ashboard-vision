using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.Extensions;
using Trofeo.Extensions.Service;
using Trofeo.ModuleKit;
using Trofeo.TestDoubles;
using static Trofeo.Display.Tests.LoopRig;

namespace Trofeo.Display.Tests;

/// <summary>Le registre des types de modules, modifiable pour les modules complémentaires, et ce que la boucle en fait (les tests du registre d'origine sont dans DisplayLoopTests.cs).</summary>
public sealed class ModuleRegistryCommunityTests
{
    private sealed class StubFactory(string typeId) : IModuleFactory
    {
        public ModuleDescriptor Descriptor { get; } = new(typeId, typeId, new GridSize(1, 1), new Dictionary<SizePreset, GridSize>());

        public IModule Create(ModuleContext context) => new FakeModule { Descriptor = Descriptor };
    }

    private static ModuleRegistry NewRegistry(params string[] builtIn) =>
        new(builtIn.Select(id => (IModuleFactory)new StubFactory(id)), _ => new FakeSecretStore());

    private static CommunityEntry Entry(string id, CommunityState state, string? message = null) =>
        new(id, @"C:\modules\" + id, new ModuleManifest(id, "Météo", "Une météo.", "acme", "1.0.0", new SdkVersion(1, 0), "Acme.dll", [], null,
            new GridSize(2, 1), new Dictionary<SizePreset, GridSize>(), SettingsSchema.Empty, null), state, message, null, false);

    [Fact]
    public void Registry_SetCommunityFactories_KeepsBuiltInsFirstAndRaisesTypesChanged()
    {
        var registry = NewRegistry("clock", "monitoring");
        var changes = 0;
        registry.TypesChanged += (_, _) => changes++;

        registry.SetCommunityFactories([new StubFactory("acme.weather"), new StubFactory("zed.logs")]);

        Assert.Equal(["clock", "monitoring", "acme.weather", "zed.logs"], registry.Types.Select(t => t.TypeId));
        Assert.Equal(["acme.weather", "clock", "monitoring", "zed.logs"], registry.Descriptors.Keys.Order(StringComparer.Ordinal));
        Assert.NotNull(registry.Factory("acme.weather"));
        Assert.Equal(1, changes);

        // Le remplacement est d'un bloc : l'ancienne liste disparaît, les types d'origine restent en tête.
        registry.SetCommunityFactories([new StubFactory("other.thing")]);

        Assert.Equal(["clock", "monitoring", "other.thing"], registry.Types.Select(t => t.TypeId));
        Assert.Null(registry.Factory("acme.weather"));
        Assert.NotNull(registry.Factory("clock"));
        Assert.Equal(2, changes);

        registry.SetCommunityFactories([]);
        Assert.Equal(["clock", "monitoring"], registry.Types.Select(t => t.TypeId));
        Assert.Equal(3, changes);
    }

    [Fact]
    public void Registry_CommunityTypeWithABuiltInId_DoesNotReplaceIt()
    {
        var registry = NewRegistry("clock");
        var original = registry.Factory("clock");

        registry.SetCommunityFactories([new StubFactory("clock"), new StubFactory("acme.one"), new StubFactory("acme.one")]);

        Assert.Same(original, registry.Factory("clock"));
        Assert.Equal(["clock", "acme.one"], registry.Types.Select(t => t.TypeId));
    }

    [Fact]
    public async Task Registry_AReaderSeesOneSnapshot_WhileFactoriesAreReplaced()
    {
        var registry = NewRegistry("clock");
        var failures = new List<string>();
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            for (var i = 0; !stop.IsCancellationRequested; i++)
            {
                registry.SetCommunityFactories(i % 2 == 0 ? [new StubFactory("a.one"), new StubFactory("a.two")] : [new StubFactory("b.one")]);
            }
        });
        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 20_000; i++)
            {
                var descriptors = registry.Descriptors; // une lecture = un instantané complet
                if (!descriptors.ContainsKey("clock") || descriptors.Count is not (1 or 2 or 3))
                {
                    lock (failures)
                    {
                        failures.Add($"{descriptors.Count} types : {string.Join(",", descriptors.Keys)}");
                    }
                }

                var types = registry.Types;
                if (types[0].TypeId != "clock" || types.Count is not (1 or 2 or 3))
                {
                    lock (failures)
                    {
                        failures.Add($"{types.Count} types dans la liste");
                    }
                }
            }
        })).ToArray();

        await Task.WhenAll(readers);
        await stop.CancelAsync();
        await writer;

        Assert.Empty(failures);
    }

    [Fact]
    public void Registry_Create_UsesTheCurrentCommunityFactories()
    {
        var registry = NewRegistry("clock");
        var placement = Place(A, "acme.weather");
        registry.SetCommunityFactories([new StubFactory("acme.weather")]);

        Assert.NotNull(registry.Create(placement, TimeProvider.System));

        registry.SetCommunityFactories([]);
        Assert.Null(registry.Create(placement, TimeProvider.System));
    }

    private static async Task<IReadOnlyList<string>> ApplyAndTick(LoopRig rig, DashboardLayout layout)
    {
        var task = rig.Loop.ApplyAsync(new LoopConfig(layout, Themes.ClassiqueDefinition, new ColorBackground(SKColors.Black)));
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Loop.Tick();
        return await task;
    }

    [Fact]
    public async Task Layout_WithUnapprovedCommunityType_ShowsPlaceholder()
    {
        using var rig = new LoopRig();
        rig.AddHealthyScreen();
        rig.Registry.SetCommunityFactories([new PlaceholderModuleFactory(Entry("acme.weather", CommunityState.NeedsApproval, "Ce module n'a pas encore été validé."))]);

        var warnings = await ApplyAndTick(rig, Layout(Place(A, "fake"), Place(B, "acme.weather", column: 6, columns: 4, rows: 2)));

        Assert.Empty(warnings); // la disposition ne refuse pas le module : sa case est là
        var status = rig.Loop.Status;
        var placeholder = Assert.Single(status.Modules, module => module.InstanceId == B);
        Assert.Equal(new ModuleStatus(ModuleHealth.NotConfigured, "À valider dans la fenêtre de réglage"), placeholder.Status);
        Assert.Contains(status.Modules, module => module.InstanceId == A); // et les autres cases n'en souffrent pas
        Assert.True(rig.Loop.FramesSent >= 1);
    }

    [Fact]
    public async Task Layout_WithUninstalledType_IsIgnoredWithWarning()
    {
        using var rig = new LoopRig();
        rig.AddHealthyScreen();

        var warnings = await ApplyAndTick(rig, Layout(Place(A, "fake"), Place(B, "acme.gone", column: 6)));

        var warning = Assert.Single(warnings);
        Assert.Contains("type inconnu « acme.gone »", warning);
        var status = rig.Loop.Status;
        Assert.Equal([A], status.Modules.Select(module => module.InstanceId)); // le rechargement ne tombe pas, les autres cases restent
    }

    [Fact]
    public async Task Layout_PlaceholderBecomesAnotherModule_WhenTheTypeIsRegisteredAgainAndTheLayoutReloaded()
    {
        using var rig = new LoopRig();
        rig.AddHealthyScreen();
        var layout = Layout(Place(A, "fake"), Place(B, "acme.weather", column: 6));
        rig.Registry.SetCommunityFactories([new PlaceholderModuleFactory(Entry("acme.weather", CommunityState.NeedsApproval))]);
        await ApplyAndTick(rig, layout);
        Assert.Equal(ModuleHealth.NotConfigured, rig.Loop.Status.Modules.Single(module => module.InstanceId == B).Status.Health);

        // Validé : la fabrique du module remplace la case de remplacement (le service recharge la disposition quand les types changent).
        rig.Registry.SetCommunityFactories([new StubFactory("acme.weather")]);
        await ApplyAndTick(rig, Layout(Place(A, "fake"), Place(B, "acme.weather", column: 6, settings: """{"ville":"Lyon"}""")));

        Assert.Equal(ModuleHealth.Online, rig.Loop.Status.Modules.Single(module => module.InstanceId == B).Status.Health);
    }
}

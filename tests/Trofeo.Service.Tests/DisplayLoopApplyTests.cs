using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.ModuleKit;
using static Trofeo.Display.Tests.LoopRig;

namespace Trofeo.Display.Tests;

public sealed class DisplayLoopApplyTests
{
    private static LoopConfig Config(DashboardLayout layout, ThemeDefinition? theme = null, IBackground? background = null) =>
        new(layout, theme ?? Themes.ClassiqueDefinition, background ?? new ColorBackground(SKColors.Black));

    private static async Task<IReadOnlyList<string>> ApplyAndTick(LoopRig rig, LoopConfig config)
    {
        var task = rig.Loop.ApplyAsync(config);
        rig.Time.Advance(TimeSpan.FromSeconds(1)); // laisse passer le plafond d'images
        rig.Loop.Tick();
        return await task;
    }

    [Fact]
    public async Task Apply_MovedModule_IsKeptNotRecreated()
    {
        using var rig = new LoopRig();
        rig.AddHealthyScreen();
        rig.Loop.Start(CancellationToken.None);
        rig.Loop.Tick();
        var module = Assert.Single(rig.Created);
        var before = module.Rects.Last();

        var warnings = await ApplyAndTick(rig, Config(Layout(Place(A, column: 6, row: 1, columns: 5, rows: 1))));

        Assert.Empty(warnings);
        Assert.Single(rig.Created);
        Assert.Equal(0, module.Disposed);
        Assert.Equal(1, module.Starts);
        Assert.NotEqual(before, module.Rects.Last()); // nouveau rectangle au rendu suivant
        Assert.Equal(6, rig.Loop.Layout.Views[0].Placements[0].Column);
    }

    [Fact]
    public async Task Apply_ChangedSettings_RecreatesModule()
    {
        using var rig = new LoopRig();
        rig.AddHealthyScreen();
        rig.Loop.Start(CancellationToken.None);
        rig.Loop.Tick();
        var old = Assert.Single(rig.Created);

        await ApplyAndTick(rig, Config(Layout(Place(A, settings: """{"zone":"Paris"}"""))));

        Assert.Equal(2, rig.Created.Count);
        Assert.Equal(1, old.Disposed);
        Assert.Equal(1, rig.Created[1].Starts);
        Assert.Equal(0, rig.Created[1].Disposed);
        Assert.NotNull(rig.Loop.FindCommandTarget(A));
    }

    [Fact]
    public async Task Apply_EqualSettingsWrittenDifferently_AreKept()
    {
        using var rig = new LoopRig(Layout(Place(A, settings: """{"a":1,"b":[1,2]}""")));
        rig.AddHealthyScreen();
        rig.Loop.Tick();

        await ApplyAndTick(rig, Config(Layout(Place(A, settings: """{ "a": 1, "b": [1, 2] }"""))));

        Assert.Single(rig.Created);
        Assert.Equal(0, rig.Created[0].Disposed);
    }

    [Fact]
    public async Task Apply_ChangedType_RecreatesModule()
    {
        using var rig = new LoopRig();
        rig.AddHealthyScreen();
        rig.Loop.Tick();

        await ApplyAndTick(rig, Config(Layout(Place(A, "other"))));

        Assert.Equal(2, rig.Created.Count);
        Assert.Equal(1, rig.Created[0].Disposed);
        Assert.Equal("other", rig.Created[1].Descriptor.TypeId);
    }

    [Fact]
    public async Task Apply_NewTextSize_IsReadFromNewLayout()
    {
        using var rig = new LoopRig();
        rig.AddHealthyScreen();
        rig.Loop.Tick();

        await ApplyAndTick(rig, Config(Layout(Place(A, textSize: TextSize.Large))));

        Assert.Single(rig.Created);
        Assert.Equal(TextSize.Large, rig.Loop.Layout.Views[0].Placements[0].TextSize);
    }

    [Fact]
    public async Task Apply_RemovedModule_IsDisposed()
    {
        using var rig = new LoopRig(Layout(Place(A, columns: 4), Place(B, "other", column: 4, columns: 4)));
        rig.AddHealthyScreen();
        rig.Loop.Start(CancellationToken.None);
        rig.Loop.Tick();

        await ApplyAndTick(rig, Config(Layout(Place(A, columns: 4))));

        Assert.Equal(0, rig.Of(A).Single().Disposed);
        Assert.Equal(1, rig.Of(B).Single().Disposed);
        Assert.Same(rig.Of(A).Single(), rig.Loop.FindCommandTarget(B)); // B n'existe plus : repli sur le premier module qui accepte les commandes
        Assert.Single(rig.Loop.Status.Modules);
    }

    [Fact]
    public async Task Apply_AddedModule_IsCreatedAndStarted()
    {
        using var rig = new LoopRig();
        rig.AddHealthyScreen();
        rig.Loop.Start(CancellationToken.None);
        rig.Loop.Tick();

        await ApplyAndTick(rig, Config(Layout(Place(A, columns: 4), Place(B, "other", column: 4, columns: 4))));

        Assert.Equal(1, rig.Of(B).Single().Starts);
        Assert.Equal(2, rig.Loop.Status.Modules.Count);
    }

    [Fact]
    public async Task Apply_BeforeStart_DoesNotStartNewModules_StartDoesLater()
    {
        using var rig = new LoopRig();
        rig.AddHealthyScreen();

        await ApplyAndTick(rig, Config(Layout(Place(A, settings: """{"x":1}"""))));
        Assert.Equal(0, rig.Created[1].Starts);

        rig.Loop.Start(CancellationToken.None);

        Assert.Equal(1, rig.Created[1].Starts);
        Assert.Equal(0, rig.Created[0].Starts); // l'ancien a été libéré avant le démarrage
    }

    [Fact]
    public async Task Apply_NewTheme_ReplacesBackgroundAndDisposesOld()
    {
        var first = new TrackedBackground(SKColors.Red);
        var second = new TrackedBackground(SKColors.Blue);
        using var rig = new LoopRig(background: first);
        rig.AddHealthyScreen();
        rig.Loop.Tick();
        var jpegBefore = rig.Screen.LastFrame;

        await ApplyAndTick(rig, Config(Layout(Place(A)), Themes.Pixel, second));

        Assert.True(first.Disposed);
        Assert.False(second.Disposed);
        Assert.Equal(2, rig.Loop.FramesSent);
        Assert.NotEqual(jpegBefore, rig.Screen.LastFrame); // nouvelle image, fond et thème changés

        rig.Loop.Dispose();
        Assert.True(second.Disposed); // la boucle est propriétaire du nouveau fond
    }

    [Fact]
    public async Task Apply_SameBackgroundInstance_IsNotDisposed()
    {
        var background = new TrackedBackground(SKColors.Red);
        using var rig = new LoopRig(background: background);
        rig.AddHealthyScreen();
        rig.Loop.Tick();

        await ApplyAndTick(rig, Config(Layout(Place(A)), background: background));

        Assert.False(background.Disposed);
    }

    [Fact]
    public async Task Apply_UnknownType_ReturnsWarningKeepsOthers()
    {
        using var rig = new LoopRig();
        rig.AddHealthyScreen();
        rig.Loop.Tick();

        var warnings = await ApplyAndTick(rig, Config(Layout(
            Place(A, columns: 4), Place(B, "zzz", column: 4, columns: 4))));

        Assert.Equal(["Module ignoré : type inconnu « zzz »."], warnings);
        Assert.Equal(0, rig.Created[0].Disposed);
        Assert.Single(rig.Loop.Status.Modules);
    }

    [Fact]
    public async Task Apply_OverlappingPlacements_AreRefusedWithWarnings()
    {
        using var rig = new LoopRig();
        rig.AddHealthyScreen();
        rig.Loop.Tick();

        var warnings = await ApplyAndTick(rig, Config(Layout(
            Place(A, columns: 4), Place(B, "other", column: 2, columns: 4))));

        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w => Assert.StartsWith("Module ignoré", w));
        Assert.Empty(rig.Loop.Status.Modules);
    }

    [Fact]
    public async Task Apply_DuplicateInstanceId_SecondIsIgnoredWithWarning()
    {
        using var rig = new LoopRig();
        rig.AddHealthyScreen();
        rig.Loop.Tick();

        var warnings = await ApplyAndTick(rig, Config(Layout(
            Place(A, columns: 4), Place(A, "other", column: 4, columns: 4))));

        Assert.Single(warnings);
        Assert.Single(rig.Loop.Status.Modules);
    }

    [Fact]
    public async Task Apply_MaxFps_ChangesScheduler()
    {
        using var rig = new LoopRig();
        rig.AddHealthyScreen();
        rig.Loop.Tick();

        await ApplyAndTick(rig, Config(Layout(Place(A)) with { MaxFps = 4 }));
        Assert.Equal(250, rig.Scheduler.MinInterval.TotalMilliseconds, precision: 3);

        await ApplyAndTick(rig, Config(Layout(Place(A))));
        Assert.Equal(1000.0 / 12, rig.Scheduler.MinInterval.TotalMilliseconds, precision: 3);
    }

    [Fact]
    public async Task Apply_RequestsAFrame()
    {
        using var rig = new LoopRig();
        rig.AddHealthyScreen();
        rig.Loop.Tick();
        Assert.Equal(1, rig.Loop.FramesSent);

        await ApplyAndTick(rig, Config(Layout(Place(A))));

        Assert.Equal(2, rig.Loop.FramesSent);
    }

    [Fact]
    public async Task Apply_TwiceBeforeTick_AppliesInOrder_EachCompletes()
    {
        using var rig = new LoopRig(Layout(Place(A, columns: 4), Place(B, "other", column: 4, columns: 4)));
        rig.AddHealthyScreen();
        rig.Loop.Start(CancellationToken.None);
        rig.Loop.Tick();

        var first = rig.Loop.ApplyAsync(Config(Layout(Place(A, columns: 4)))); // retire B
        var second = rig.Loop.ApplyAsync(Config(Layout(Place(A, columns: 4), Place(B, "other", column: 4, columns: 4)))); // le remet
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Loop.Tick();

        Assert.Empty(await first);
        Assert.Empty(await second);
        Assert.Equal(2, rig.Of(B).Count()); // le second apparaît par rapport à l'état après le premier : B est recréé
        Assert.Equal(1, rig.Of(B).First().Disposed);
        Assert.Equal(0, rig.Of(B).Last().Disposed);
        Assert.Equal(0, rig.Of(A).Single().Disposed);
    }

    [Fact]
    public async Task Apply_NotCompletedUntilTick()
    {
        using var rig = new LoopRig();
        rig.Scheduler.Signal.WaitOne(0); // vide le signal de la construction
        var task = rig.Loop.ApplyAsync(Config(Layout(Place(A))));

        Assert.False(task.IsCompleted);
        Assert.True(rig.Scheduler.Signal.WaitOne(0)); // la boucle est réveillée

        rig.Loop.Tick();
        Assert.True(task.IsCompleted);
        await task;
    }

    [Fact]
    public async Task Apply_WorksWithoutScreen()
    {
        using var rig = new LoopRig();

        var warnings = await ApplyAndTick(rig, Config(Layout(Place(A, settings: """{"x":1}"""))));

        Assert.Empty(warnings);
        Assert.Equal(2, rig.Created.Count);
    }

    [Fact]
    public async Task Apply_AfterDispose_CompletesEmpty_AndDisposesBackground()
    {
        var rig = new LoopRig();
        rig.Dispose();
        var background = new TrackedBackground(SKColors.Red);

        var warnings = await rig.Loop.ApplyAsync(Config(Layout(Place(A)), background: background));

        Assert.Empty(warnings);
        Assert.True(background.Disposed);
    }

    [Fact]
    public async Task Dispose_WithPendingApply_CompletesIt()
    {
        var rig = new LoopRig();
        var background = new TrackedBackground(SKColors.Red);
        var task = rig.Loop.ApplyAsync(Config(Layout(Place(A)), background: background));

        rig.Dispose();

        Assert.Empty(await task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(background.Disposed);
    }

    [Fact]
    public async Task Dispose_WithPendingApplyKeepingCurrentBackground_DisposesItOnce()
    {
        var background = new TrackedBackground(SKColors.Red);
        var rig = new LoopRig(background: background);
        // Un rechargement qui garde le fond le rend tel quel : annulé à l'arrêt, il ne doit pas le libérer une seconde fois.
        var task = rig.Loop.ApplyAsync(Config(Layout(Place(A)), background: background));

        rig.Dispose();

        Assert.Empty(await task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, background.DisposeCount);
    }

    [Fact]
    public async Task Apply_AfterDispose_WithCurrentBackground_DoesNotDisposeItAgain()
    {
        var background = new TrackedBackground(SKColors.Red);
        var rig = new LoopRig(background: background);
        rig.Dispose();

        await rig.Loop.ApplyAsync(Config(Layout(Place(A)), background: background));

        Assert.Equal(1, background.DisposeCount);
    }

    [Fact]
    public async Task Restart_RecreatesInstance()
    {
        using var rig = new LoopRig();
        rig.AddHealthyScreen();
        rig.Loop.Start(CancellationToken.None);
        rig.Loop.Tick();
        var old = Assert.Single(rig.Created);

        var task = rig.Loop.RestartAsync(A);
        Assert.False(task.IsCompleted);
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Loop.Tick();

        Assert.True(await task);
        Assert.Equal(2, rig.Created.Count);
        Assert.Equal(1, old.Disposed);
        Assert.Equal(1, rig.Created[1].Starts);
        Assert.Same(rig.Created[1], rig.Loop.FindCommandTarget(A));
        Assert.Equal(2, rig.Loop.FramesSent); // une image est redemandée
    }

    [Fact]
    public async Task Restart_Unknown_ReturnsFalse()
    {
        using var rig = new LoopRig();
        rig.AddHealthyScreen();
        rig.Loop.Tick();

        var task = rig.Loop.RestartAsync(B);
        rig.Loop.Tick();

        Assert.False(await task);
        Assert.Single(rig.Created);
        Assert.Equal(0, rig.Created[0].Disposed);
    }

    [Fact]
    public async Task Restart_AfterDispose_ReturnsFalse()
    {
        var rig = new LoopRig();
        rig.Dispose();

        Assert.False(await rig.Loop.RestartAsync(A));
    }

    [Fact]
    public async Task FindCommandTarget_DuringApply_IsSafe()
    {
        using var rig = new LoopRig(Layout(Place(A, columns: 4), Place(B, "other", column: 4, columns: 4)));
        rig.AddHealthyScreen(acks: 2000);
        rig.Loop.Tick();
        using var stop = new CancellationTokenSource();
        Exception? failure = null;
        var reader = Task.Run(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    rig.Loop.FindCommandTarget(A);
                    rig.Loop.FindCommandTarget(null);
                    _ = rig.Loop.Status;
                    _ = rig.Loop.Layout;
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        for (var i = 0; i < 40; i++) // moins de 100 images : la ligne de journal « Image 100 » est celle d'un autre test
        {
            var layout = i % 2 == 0
                ? Layout(Place(A, columns: 4, settings: $$"""{"n":{{i}}}"""))
                : Layout(Place(A, columns: 4), Place(B, "other", column: 4, columns: 4));
            var task = rig.Loop.ApplyAsync(Config(layout));
            rig.Time.Advance(TimeSpan.FromSeconds(1));
            rig.Loop.Tick();
            Assert.True(task.IsCompleted);
        }

        stop.Cancel();
        await reader.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(failure);
    }

    [Fact]
    public void Status_ReportsModulesNoticesAndCounters()
    {
        using var rig = new LoopRig(Layout(Place(A, columns: 4), Place(B, "other", column: 4, columns: 4)));
        rig.AddHealthyScreen();
        rig.Of(A).Single().Notices = ["Une remarque."];
        rig.Of(B).Single().Status = new ModuleStatus(ModuleHealth.Offline, "Hors ligne");
        rig.Loop.Tick();

        var status = rig.Loop.Status;

        Assert.False(status.Emulated);
        Assert.Equal(1, status.FramesSent);
        Assert.True(status.RenderMs >= 0);
        Assert.Equal([A, B], status.Modules.Select(m => m.InstanceId));
        Assert.Equal(["Une remarque."], status.Modules[0].Notices);
        Assert.Empty(status.Modules[1].Notices);
        Assert.Equal(ModuleHealth.Offline, status.Modules[1].Status.Health);
        Assert.Equal("other", status.Modules[1].TypeId);
    }
}

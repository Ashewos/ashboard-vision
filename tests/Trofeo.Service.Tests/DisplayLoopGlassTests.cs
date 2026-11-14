using SkiaSharp;
using Trofeo.Core.Rendering;
using static Trofeo.Display.Tests.LoopRig;

namespace Trofeo.Display.Tests;

/// <summary>La boucle et le verre de « Liquid Glass avancé », calculé sur un fil à part (plan 9, tâche 3).</summary>
public sealed class DisplayLoopGlassTests
{
    private static ThemeDefinition Advanced(string preset) =>
        Themes.LiquidGlassAdvanced with { Panel = Themes.LiquidGlassAdvanced.Panel with { Glass = GlassPresets.Find(preset) } };

    private static void WaitForDirty(LoopRig rig) =>
        Assert.True(SpinWait.SpinUntil(() => rig.Scheduler.IsDirty, TimeSpan.FromSeconds(30)), "aucune image redemandée");

    [Fact]
    public void AdvancedTheme_GlassReady_AsksForANewFrame()
    {
        using var rig = new LoopRig(theme: Advanced(GlassPresets.Clair), background: new ColorBackground(SKColors.DarkSlateBlue));
        rig.AddHealthyScreen();
        rig.Loop.Start(CancellationToken.None);

        rig.Loop.Tick(); // première image, sans attendre le verre : son calcul part sur le fil à part
        int sent = rig.Loop.FramesSent;
        Assert.Equal(1, sent);

        WaitForDirty(rig); // la couche est prête : la boucle est réveillée
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Loop.Tick();

        Assert.Equal(sent + 1, rig.Loop.FramesSent);
    }

    [Fact]
    public async Task AdvancedTheme_GlassSettingChange_KeepsWorkingOnTheSameBackground()
    {
        var background = new TrackedBackground(SKColors.DarkSlateBlue);
        using var rig = new LoopRig(theme: Advanced(GlassPresets.Clair), background: background);
        rig.AddHealthyScreen();
        rig.Loop.Start(CancellationToken.None);
        rig.Loop.Tick();
        WaitForDirty(rig);

        // Un autre préréglage sur le même fond : le compositeur reste, et le nouveau verre arrive à son tour.
        var task = rig.Loop.ApplyAsync(new LoopConfig(rig.Loop.Layout, Advanced(GlassPresets.Prisme), background));
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Loop.Tick();
        Assert.Empty(await task);
        int sent = rig.Loop.FramesSent;

        WaitForDirty(rig);
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Loop.Tick();

        Assert.Equal(sent + 1, rig.Loop.FramesSent);
        Assert.False(background.Disposed);
    }
}

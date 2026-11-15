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
    public async Task AdvancedTheme_GlassSettingChange_ShowsThePreviousLayerWhileTheNewOneComputes()
    {
        var background = new TrackedBackground(SKColors.DarkSlateBlue);
        using var rig = new LoopRig(theme: Advanced(GlassPresets.Clair), background: background);
        rig.AddHealthyScreen();
        var frames = new List<byte[]>();
        using var subscription = rig.Loop.SubscribePreview(12, frame => { lock (frames) frames.Add(frame.Jpeg); });
        rig.Loop.Start(CancellationToken.None);

        rig.Loop.Tick(); // 0 : le verre simple, le calcul de Clair part
        WaitForDirty(rig);
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Loop.Tick(); // 1 : le verre de Clair
        Assert.Equal(2, frames.Count);
        Assert.NotEqual(frames[0], frames[1]);

        // Un autre préréglage sur le même fond. L'image composée juste après ne peut pas avoir le nouveau verre (son calcul
        // part pendant cette composition) : elle doit montrer la couche précédente, celle de Clair, et non le verre simple.
        var task = rig.Loop.ApplyAsync(new LoopConfig(rig.Loop.Layout, Advanced(GlassPresets.Prisme), background));
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Loop.Tick(); // 2
        Assert.Empty(await task);
        Assert.Equal(3, frames.Count);
        Assert.Equal(frames[1], frames[2]);

        WaitForDirty(rig);
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Loop.Tick(); // 3 : le verre de Prisme
        Assert.Equal(4, frames.Count);
        Assert.NotEqual(frames[2], frames[3]);
        Assert.NotEqual(frames[0], frames[3]);
        Assert.False(background.Disposed);
    }
}

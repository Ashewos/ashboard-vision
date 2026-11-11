using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.Core.Shortcuts;
using static Trofeo.Display.Tests.LoopRig;

namespace Trofeo.Display.Tests;

// Les vues vues par le service : l'état, le changement de vue demandé par la fenêtre, l'annonce des changements et l'aperçu d'une vue.
public sealed partial class ServiceControlHandlerTests
{
    private static readonly Guid V1 = new("11110000-0000-0000-0000-000000000001");
    private static readonly Guid V2 = new("22220000-0000-0000-0000-000000000002");
    private static readonly Guid V3 = new("33330000-0000-0000-0000-000000000003");
    private static readonly Guid Q1 = new("11110000-0000-0000-0000-0000000000a1");
    private static readonly Guid Q2 = new("22220000-0000-0000-0000-0000000000a2");
    private static readonly Guid Q3 = new("33330000-0000-0000-0000-0000000000a3");

    private static DashboardLayout ThreeViewLayout() => new(
        180,
        [
            new LayoutView(V1, "Vue 1", [Place(Q1, "fake")]),
            new LayoutView(V2, "Vue 2", [Place(Q2, "other", column: 6)]),
            new LayoutView(V3, "Vue 3", [Place(Q3, "fake")]),
        ]);

    [Fact]
    public void Status_CarriesTheDisplayedView()
    {
        using var rig = new Rig(ThreeViewLayout());

        Assert.Equal(V1, rig.Handler.GetStatus().CurrentViewId);

        rig.LoopRig.Loop.ShowAdjacentView(+1);

        Assert.Equal(V2, rig.Handler.GetStatus().CurrentViewId);
    }

    [Fact]
    public async Task ShowView_UnknownId_ReturnsFalse()
    {
        using var rig = new Rig(ThreeViewLayout());
        var changes = new List<Guid>();
        rig.Handler.ViewChanged += changes.Add;

        Assert.False(await rig.Handler.ShowViewAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(V1, rig.LoopRig.Loop.CurrentViewId);
        Assert.Empty(changes);
    }

    [Fact]
    public async Task ShowView_GlidesTheNaturalWay_AndTheSameViewIsANoOp()
    {
        using var rig = new Rig(ThreeViewLayout());
        var changes = new List<Guid>();
        rig.Handler.ViewChanged += changes.Add;

        Assert.True(await rig.Handler.ShowViewAsync(V3, CancellationToken.None)); // deux vues plus loin : vers la suivante
        Assert.Equal((V3, +1), (rig.LoopRig.Loop.CurrentViewId, rig.LoopRig.Loop.LastViewDirection));

        Assert.True(await rig.Handler.ShowViewAsync(V2, CancellationToken.None)); // en arrière
        Assert.Equal((V2, -1), (rig.LoopRig.Loop.CurrentViewId, rig.LoopRig.Loop.LastViewDirection));

        Assert.True(await rig.Handler.ShowViewAsync(V2, CancellationToken.None)); // déjà affichée : vrai, sans rien faire
        Assert.Equal([V3, V2], changes);
    }

    [Fact]
    public void ViewChanged_IsRaisedOnKeyboardSwitch()
    {
        using var rig = new Rig(ThreeViewLayout());
        var changes = new List<Guid>();
        Action<Guid> subscriber = changes.Add;
        rig.Handler.ViewChanged += subscriber;

        rig.LoopRig.Loop.ShowAdjacentView(+1); // ce que fait le raccourci Droite
        rig.LoopRig.Loop.ShowAdjacentView(-1);
        rig.Handler.ViewChanged -= subscriber;
        rig.LoopRig.Loop.ShowAdjacentView(+1);

        Assert.Equal([V2, V1], changes);
    }

    [Fact]
    public void SubscribePreview_UnknownView_IsAnError()
    {
        using var rig = new Rig(ThreeViewLayout());

        var ex = Assert.Throws<InvalidOperationException>(() => rig.Handler.SubscribePreview(4, Guid.NewGuid(), _ => { }));

        Assert.Equal("Cette vue n'existe pas.", ex.Message);
    }

    [Fact]
    public void PreviewOfHiddenView_ComposesThatViewOnlyWhileSubscribed()
    {
        using var rig = new Rig(ThreeViewLayout());
        var loop = rig.LoopRig;
        var hiddenModule = loop.Of(Q2).Single();
        loop.Loop.Tick();
        Assert.Empty(hiddenModule.Rects); // la deuxième vue n'est pas affichée : rien ne la dessine

        var frames = new List<PreviewFrame>();
        var subscription = rig.Handler.SubscribePreview(4, V2, frames.Add);
        for (var i = 0; i < 200; i++)
        {
            loop.Loop.Tick();
            loop.Time.Advance(TimeSpan.FromMilliseconds(10));
        }

        Assert.NotEmpty(hiddenModule.Rects);
        Assert.NotEmpty(frames);
        Assert.Equal(1, loop.Loop.FramesSent); // l'écran n'a eu que sa première image
        Assert.NotEqual(loop.Screen.LastFrame, frames[^1].Jpeg); // et l'image de la vue cachée ne lui est jamais allée

        subscription.Dispose();
        var rects = hiddenModule.Rects.Count;
        for (var i = 0; i < 200; i++)
        {
            loop.Loop.Tick();
            loop.Time.Advance(TimeSpan.FromMilliseconds(10));
        }

        Assert.Equal(rects, hiddenModule.Rects.Count); // plus d'abonné : la vue cachée n'est plus composée
    }

    [Fact]
    public async Task Reload_WithoutAnyCommandableCase_StillAppliesTheShortcuts()
    {
        using var rig = new Rig();
        rig.Store.Save(Layout()); // aucune case : donc aucune case qui accepte les commandes

        var result = await rig.Pump(rig.Handler.ReloadAsync(CancellationToken.None));

        Assert.True(result.Applied);
        var applied = Assert.Single(rig.Hotkeys.Applied);
        Assert.Same(ShortcutDefaults.All, applied); // les raccourcis de vue n'ont pas besoin d'un module commandable
    }
}

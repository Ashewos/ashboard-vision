using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AshboardVision.Sdk;
using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.Settings.Controls;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels;
using Trofeo.Settings.Views;
using static Trofeo.Settings.Tests.CommunityFixture;
using static Trofeo.Settings.Tests.ModulesFixture;

namespace Trofeo.Settings.Tests;

/// <summary>Le point d'exclamation jaune des modules complémentaires : dans les listes, dans l'en-tête des réglages, dans un angle de la case.</summary>
public sealed class CommunityBadgeTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];
    private Window? _window;

    public void Dispose()
    {
        _window?.Close();
        foreach (var disposable in _disposables)
            disposable.Dispose();
    }

    [Fact]
    public void Badge_ShownForCommunityTypesOnly()
    {
        var fixture = new ModulesFixture(_ => WithDemo());
        _disposables.Add(fixture);
        fixture.Link.SetTypes([.. SampleData.Types(), DemoType()]);

        // La liste des modules (onglet Modules) : seuls les types de la communauté.
        Assert.True(fixture.Tab.Placed.Single(p => p.TypeId == "alex.demo").IsCommunity);
        Assert.All(fixture.Tab.Placed.Where(p => p.TypeId != "alex.demo"), p => Assert.False(p.IsCommunity));
        Assert.All(fixture.Tab.Shelved, p => Assert.True(p.IsCommunity));

        // L'en-tête des réglages du module : la mention, l'auteur, la version et les permissions accordées.
        fixture.Tab.Select(DemoId);
        Assert.True(fixture.Tab.SelectedIsCommunity);
        Assert.Equal("Module complémentaire : créé par la communauté, pas par Ashboard Vision", fixture.Tab.SelectedCommunityNotice);
        Assert.Equal("par Alex · version 1.2.0", fixture.Tab.SelectedCommunityDetails);
        Assert.Equal("Permissions accordées : Internet.", fixture.Tab.SelectedCommunityPermissions);
        fixture.Tab.Select(ClaudeId);
        Assert.False(fixture.Tab.SelectedIsCommunity);
        Assert.Equal("", fixture.Tab.SelectedCommunityDetails);

        // La liste « Ajouter un module » et la liste des modules placés de l'onglet Disposition.
        var layoutTab = new LayoutTabViewModel(fixture.Link, fixture.Session);
        Assert.Equal(["alex.demo"], layoutTab.AddTypes.Where(t => t.IsCommunity).Select(t => t.TypeId));
        Assert.Equal(7, layoutTab.AddTypes.Count);
        Assert.True(layoutTab.Placed.Single(p => p.TypeId == "alex.demo").IsCommunity);
        Assert.All(layoutTab.Placed.Where(p => p.TypeId != "alex.demo"), p => Assert.False(p.IsCommunity));
        layoutTab.Dispose();
    }

    [Fact]
    public void Badge_InstalledAfterTheConnection_ShowsUpWithoutReconnecting()
    {
        var fixture = new ModulesFixture(_ => WithDemo());
        _disposables.Add(fixture);

        // Au départ le service n'annonce que ses six types : la case du module est inconnue.
        Assert.False(fixture.Tab.Placed.Single(p => p.TypeId == "alex.demo").IsCommunity);

        // Il vient d'être installé : le service annonce un type de plus (types relus), sans nouvelle poignée de main.
        fixture.Link.SetTypes([.. SampleData.Types(), DemoType()]);

        Assert.True(fixture.Tab.Placed.Single(p => p.TypeId == "alex.demo").IsCommunity);
        Assert.Equal("Démo", fixture.Tab.Placed.Single(p => p.TypeId == "alex.demo").Name);
    }

    [Fact]
    public void Badge_NameOfACommunityType_IsCleaned()
    {
        var fixture = new ModulesFixture(_ => WithDemo());
        _disposables.Add(fixture);

        fixture.Link.SetTypes([.. SampleData.Types(), DemoType(name: "Dé\u202Emo\u200B")]);

        Assert.Equal("Démo", fixture.Tab.Placed.Single(p => p.TypeId == "alex.demo").Name);
    }

    // ---- Dans un angle de la case ----

    private static (byte R, byte G, byte B) PixelAt(WriteableBitmap bitmap, int x, int y)
    {
        using var frame = bitmap.Lock();
        var offset = y * frame.RowBytes + x * 4;
        var bytes = new byte[4];
        System.Runtime.InteropServices.Marshal.Copy(frame.Address + offset, bytes, 0, 4);
        return frame.Format == PixelFormat.Rgba8888 ? (bytes[0], bytes[1], bytes[2]) : (bytes[2], bytes[1], bytes[0]);
    }

    private static bool IsAmber((byte R, byte G, byte B) c) => c.R > 200 && c.G is > 150 and < 215 && c.B < 130;

    [AvaloniaFact]
    public void Badge_DrawnInACornerOfTheCaseOfACommunityModule_AndNowhereElse()
    {
        var fixture = new CommunityFixture();
        _disposables.Add(fixture);
        var tab = new LayoutTabViewModel(fixture.Link, fixture.Session);
        _disposables.Add(tab);
        var strip = new ScreenStrip();
        var overlay = new GridOverlay { Strip = strip, Model = tab };
        var grid = new Grid { Margin = new Thickness(24), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        grid.Children.Add(strip);
        grid.Children.Add(overlay);
        _window = new Window { Width = 1200, Height = 500, Content = grid };
        _window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        // Le centre du point d'exclamation de chaque case, en pixels de la fenêtre ; on lit le disque, à côté du trait.
        Point Badge(PlacedModuleItem item)
        {
            var px = GridGeometry.ModuleRect(item.Column, item.Row, item.Columns, item.Rows, tab.Spacing);
            var cell = strip.ScreenToStrip(new Rect(px.Left, px.Top, px.Width, px.Height));
            return strip.TranslatePoint(GridOverlay.BadgeCenter(cell), _window)!.Value;
        }

        var frame = _window.CaptureRenderedFrame()!;
        var community = Badge(tab.Placed.Single(p => p.IsCommunity));
        Assert.True(IsAmber(PixelAt(frame, (int)community.X - 4, (int)community.Y)), "le point d'exclamation jaune manque dans l'angle de la case");

        // Les autres cases n'ont rien d'autre qu'un contour : même endroit, pas de jaune.
        foreach (var other in tab.Placed.Where(p => !p.IsCommunity))
            Assert.False(IsAmber(PixelAt(frame, (int)Badge(other).X - 4, (int)Badge(other).Y)));
    }

    [AvaloniaFact]
    public void Badge_Control_HasAnAccessibleNameAndATooltip()
    {
        var badge = new CommunityBadge { Size = 20 };
        var host = new Window { Width = 100, Height = 100, Content = badge };
        host.Show();
        _window = host;

        Assert.Equal("Module complémentaire : créé par la communauté, pas par Ashboard Vision", Avalonia.Automation.AutomationProperties.GetName(badge));
        Assert.Equal(20, badge.DesiredSize.Width);
    }

    // ---- Dans la vraie fenêtre ----

    private (MainViewModel Model, CommunityFixture Fixture) OpenWindow(Action<CommunityFixture>? arrange = null)
    {
        var fixture = new CommunityFixture();
        _disposables.Add(fixture);
        arrange?.Invoke(fixture);
        var model = new MainViewModel(fixture.Link, fixture.Document, new EditorPreferences(FirstRunDone: true), fixture.Picker, ["Arial"]);
        _disposables.Add(model);
        _window = new MainWindow { DataContext = model };
        _window.Show();
        Dispatcher.UIThread.RunJobs();
        return (model, fixture);
    }

    private IEnumerable<T> Find<T>() where T : Visual => _window!.GetVisualDescendants().OfType<T>();

    private T Named<T>(string name) where T : Avalonia.Controls.Control => Find<T>().Single(c => c.Name == name);

    private Button ButtonLabelled(string text) =>
        Find<Button>().Single(b => b.IsEffectivelyVisible && b.Content is string label && label == text);

    private Point Center(Avalonia.Controls.Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), _window!)!.Value;

    private void Click(Avalonia.Controls.Control control)
    {
        _window!.MouseDown(Center(control), MouseButton.Left);
        _window!.MouseUp(Center(control), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Dialog_InTheWindow_ShowsTheWarningBadgeCheckboxesAndTheButtons()
    {
        var (model, fixture) = OpenWindow();
        fixture.Link.Package = Package(declared: [ModulePermission.Internet, ModulePermission.Sensors]);

        _ = model.SettingsTab.Community.OpenPackageAsync(@"C:\x\alex.demo-1.0.0.avmod");
        Dispatcher.UIThread.RunJobs();

        Assert.True(Named<Border>("DialogScrim").IsVisible);
        var dialog = Named<Border>("Dialog");
        Assert.Equal(2, dialog.GetVisualDescendants().OfType<CheckBox>().Count());
        Assert.All(dialog.GetVisualDescendants().OfType<CheckBox>(), c => Assert.False(c.IsChecked));
        Assert.Contains(dialog.GetVisualDescendants().OfType<CommunityBadge>(), b => b.IsEffectivelyVisible);
        Assert.Equal("Installer un module complémentaire", Named<TextBlock>("DialogTitle").Text);
        Assert.StartsWith("Ce module a été créé par la communauté, pas par Ashboard Vision.", Named<TextBlock>("WarningText").Text);
        Assert.Equal("Installer", (string)Named<Button>("ConfirmButton").Content!);
        Assert.Equal("Annuler", (string)Named<Button>("CancelButton").Content!);
        // Le reste de la fenêtre est grisé tant que la boîte est ouverte.
        Assert.False(Find<ListBox>().First(l => l.Classes.Contains("tabs")).IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void Dialog_InTheWindow_ErrorsHideTheInstallButton()
    {
        var (model, fixture) = OpenWindow();
        fixture.Link.Package = Package(errors: ["Le paquet est trop gros."], conflict: "Un autre module porte déjà cet identifiant.");

        _ = model.SettingsTab.Community.OpenPackageAsync(@"C:\x\alex.demo-1.0.0.avmod");
        Dispatcher.UIThread.RunJobs();

        Assert.False(Named<Button>("ConfirmButton").IsEffectivelyVisible);
        Assert.True(Named<Button>("CancelButton").IsEffectivelyVisible);
        var problems = Named<ItemsControl>("Problems").GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Equal(["Le paquet est trop gros.", "Un autre module porte déjà cet identifiant."], problems);
        Assert.DoesNotContain(Named<Border>("Dialog").GetVisualDescendants().OfType<CheckBox>(), c => c.IsEffectivelyVisible); // dans la boîte (l'onglet Disposition a sa propre case à cocher)
    }

    [AvaloniaFact]
    public void Dialog_InTheWindow_CancelButtonAndEscapeCloseIt()
    {
        var (model, fixture) = OpenWindow();
        _ = model.SettingsTab.Community.OpenPackageAsync(@"C:\x\alex.demo-1.0.0.avmod");
        Dispatcher.UIThread.RunJobs();
        Click(Named<Button>("CancelButton"));
        Assert.False(Named<Border>("DialogScrim").IsVisible);

        _ = model.SettingsTab.Community.OpenPackageAsync(@"C:\x\alex.demo-1.0.0.avmod");
        Dispatcher.UIThread.RunJobs();
        Assert.True(Named<Border>("DialogScrim").IsVisible);
        _window!.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(Named<Border>("DialogScrim").IsVisible);
        Assert.DoesNotContain(fixture.Link.ModuleCalls, call => call.StartsWith("install"));
    }

    [AvaloniaFact]
    public void Dialog_InTheWindow_ExtremelyLongTexts_StayInsideTheBox()
    {
        var (model, fixture) = OpenWindow();
        fixture.Link.Package = Package(name: new string('N', 5000), author: new string('A', 5000), description: string.Join(' ', Enumerable.Repeat("beaucoup de texte", 600)));

        _ = model.SettingsTab.Community.OpenPackageAsync(@"C:\x\alex.demo-1.0.0.avmod");
        Dispatcher.UIThread.RunJobs();

        var dialog = Named<Border>("Dialog");
        Assert.True(dialog.Bounds.Width <= 620 && dialog.Bounds.Height <= _window!.Bounds.Height);
        Assert.True(Named<TextBlock>("ModuleName").Bounds.Height < 80);
        Assert.True(Named<Button>("ConfirmButton").IsEffectivelyVisible);
        Assert.True(Center(Named<Button>("ConfirmButton")).Y < _window.Bounds.Height);
    }

    [AvaloniaFact]
    public void Section_InTheWindow_ShowsOneRowPerModuleWithItsButtons_AndTheTamperedBanner()
    {
        var (model, fixture) = OpenWindow(f =>
        {
            f.WithModules(
                Module(updateVersion: "1.3.0"),
                Module(id: "bob.meteo", name: "Météo", state: LinkCommunityState.SetAside),
                Module(id: "carl.mc", name: "Minecraft", state: LinkCommunityState.NeedsApproval));
            f.Link.Community = f.Link.Community with { StoreTampered = true };
        });
        model.SelectedTab = model.SettingsTab;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(3, Named<ItemsControl>("CommunityList").ItemCount);
        Assert.True(Named<Border>("TamperedBanner").IsEffectivelyVisible);
        Assert.Contains("valide de nouveau tes modules", Named<Border>("TamperedBanner").GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).First(t => t is not null));
        var labels = Named<ItemsControl>("CommunityList").GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => (string)b.Content!).ToList();
        Assert.Equal(3, labels.Count(l => l == "Désinstaller"));
        Assert.Equal(2, labels.Count(l => l == "Permissions…"));
        Assert.Equal(1, labels.Count(l => l == "Mettre à jour"));
        Assert.Equal(1, labels.Count(l => l == "Relancer"));
        Assert.Equal(1, labels.Count(l => l == "Valider…"));
        Assert.True(Named<Button>("InstallModuleButton").IsEnabled);
        Assert.Equal("Installer un module…", (string)Named<Button>("InstallModuleButton").Content!);
    }

    [AvaloniaFact]
    public void Section_InTheWindow_UpdateButtonOpensTheDialogAndIsGreyedWhileItDownloads()
    {
        var gate = new TaskCompletionSource<ModuleUpdatePreparation>();
        var (model, fixture) = OpenWindow(f =>
        {
            f.WithModules(Module(updateVersion: "1.3.0", updateNotes: "Du neuf."));
            f.Link.OnPrepare = _ => gate.Task;
        });
        model.SelectedTab = model.SettingsTab;
        Dispatcher.UIThread.RunJobs();

        var update = ButtonLabelled("Mettre à jour");
        update.BringIntoView(); // la section est en bas de l'onglet
        Dispatcher.UIThread.RunJobs();
        Click(update);

        Assert.True(Named<Border>("DialogScrim").IsVisible);
        Assert.True(Named<StackPanel>("Preparing").IsEffectivelyVisible);
        Assert.Equal("Téléchargement de la version 1.3.0…", Named<StackPanel>("Preparing").GetVisualDescendants().OfType<TextBlock>().First().Text);
        Assert.Equal("Du neuf.", Named<TextBlock>("ModuleNotes").Text);
        Assert.False(Named<Button>("ConfirmButton").IsEnabled);

        gate.SetResult(new ModuleUpdatePreparation(true, null, "1.3.0", "Notes du paquet.", [ModulePermission.Internet, ModulePermission.NowPlaying], [ModulePermission.NowPlaying]));
        Dispatcher.UIThread.RunJobs();
        Assert.False(Named<StackPanel>("Preparing").IsEffectivelyVisible);
        Assert.Equal(2, Named<ItemsControl>("PermissionList").GetVisualDescendants().OfType<CheckBox>().Count());
        Assert.True(Named<Button>("ConfirmButton").IsEnabled);
        Assert.True(Named<TextBlock>("NewPermissionsNote").IsEffectivelyVisible);
    }
}

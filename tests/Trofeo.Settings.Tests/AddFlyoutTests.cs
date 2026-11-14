using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Trofeo.Settings.ViewModels;
using Trofeo.Settings.Views;

namespace Trofeo.Settings.Tests;

/// <summary>La liste « Ajouter un module » de l'onglet Disposition, cliquée comme dans la vraie fenêtre.</summary>
public sealed class AddFlyoutTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];
    private Window? _window;

    public void Dispose()
    {
        _window?.Close();
        foreach (var disposable in _disposables)
            disposable.Dispose();
    }

    [AvaloniaTheory]
    [InlineData("codex")]      // un module d'origine
    [InlineData("alex.demo")]  // un module complémentaire
    public async Task Click_InTheAddList_AddsTheModule_AndClosesTheList(string typeId)
    {
        var fixture = new CommunityFixture();
        _disposables.Add(fixture);
        var tab = new LayoutTabViewModel(fixture.Link, fixture.Session);
        _disposables.Add(tab);
        // De la place : un seul module sur l'écran.
        var first = fixture.Document.Current.Views[0].Placements[0];
        await fixture.Session.ApplyAsync(layout => layout.WithPlacements(layout.Views[0].Id, [first]));
        Dispatcher.UIThread.RunJobs();

        var view = new LayoutTabView { DataContext = tab };
        _window = new Window { Width = 1400, Height = 900, Content = view };
        _window.Show();
        Dispatcher.UIThread.RunJobs();

        var add = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "AddButton");
        add.Flyout!.ShowAt(add);
        Dispatcher.UIThread.RunJobs();
        var list = Assert.IsType<ItemsControl>(((Flyout)add.Flyout).Content);
        var item = list.GetVisualDescendants().OfType<Button>().First(b => b.DataContext is AddTypeItem type && type.TypeId == typeId);

        // Le vrai clic : Button.OnClick lève Click (qui referme la liste) puis exécute la commande.
        typeof(Button).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(item, null);
        for (var i = 0; i < 20 && fixture.Document.Current.Views[0].Placements.Count == 1; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();

        Assert.Null(tab.RefusalText);
        Assert.Equal(2, fixture.Document.Current.Views[0].Placements.Count);
        Assert.Equal(typeId, fixture.Document.Current.Views[0].Placements[1].TypeId);
        Assert.False(add.Flyout.IsOpen);
    }
}

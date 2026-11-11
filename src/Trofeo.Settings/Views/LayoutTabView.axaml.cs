using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Trofeo.Settings.ViewModels;

namespace Trofeo.Settings.Views;

public partial class LayoutTabView : UserControl
{
    // Un onglet saisi puis tiré de plus de ce nombre de pixels se réordonne ; en deçà, c'est un simple clic.
    private const double DragThreshold = 8;

    private ViewTabItem? _pressedTab;
    private Point _pressedAt;
    private bool _reordering;

    static LayoutTabView()
    {
        // La saisie du nom d'une vue commence dans le champ, texte sélectionné : on tape, Entrée valide, Échap abandonne.
        Visual.IsVisibleProperty.Changed.AddClassHandler<TextBox>((box, change) =>
        {
            if (box.Name == "RenameBox" && change.NewValue is true)
                Dispatcher.UIThread.Post(() =>
                {
                    box.Focus();
                    box.SelectAll();
                });
        });
    }

    public LayoutTabView()
    {
        InitializeComponent();
        ViewTabList.AddHandler(PointerPressedEvent, OnTabsPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        ViewTabList.AddHandler(PointerMovedEvent, OnTabsMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        ViewTabList.AddHandler(PointerReleasedEvent, OnTabsReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private LayoutTabViewModel? Model => DataContext as LayoutTabViewModel;

    /// <summary>
    /// Un type a été choisi dans la liste « Ajouter un module » : la liste se referme, mais seulement après la commande qui ajoute
    /// le module. Le bouton lève Click avant d'exécuter sa commande ; refermée tout de suite, la liste détacherait le bouton, sa
    /// commande (liée à son contexte) tomberait à null et le clic n'ajouterait rien.
    /// </summary>
    private void OnAddTypeClicked(object? sender, RoutedEventArgs e) => Dispatcher.UIThread.Post(() => AddButton.Flyout?.Hide());

    // ---- Les onglets de vues ----

    /// <summary>Le bouton « ⋯ » d'un onglet : le seul qui porte le menu de cette vue.</summary>
    private Button? MenuButtonOf(ViewTabItem tab) =>
        ViewTabList.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "TabMenu" && ReferenceEquals(b.DataContext, tab));

    /// <summary>Une ligne du menu d'un onglet a été choisie : le menu se referme après la commande (comme la liste d'ajout).</summary>
    private void OnViewMenuItemClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is StyledElement { DataContext: ViewTabItem tab })
            Dispatcher.UIThread.Post(() => MenuButtonOf(tab)?.Flyout?.Hide());
    }

    /// <summary>Clic droit sur un onglet : la vue est choisie, puis son menu s'ouvre.</summary>
    private async void OnViewTabPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not StyledElement { DataContext: ViewTabItem tab } || !e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            return;

        e.Handled = true;
        if (Model is { } model && !tab.IsSelected)
            await model.SelectViewAsync(tab);
        Dispatcher.UIThread.Post(() =>
        {
            if (MenuButtonOf(tab) is { } button)
                button.Flyout?.ShowAt(button);
        });
    }

    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not StyledElement { DataContext: ViewTabItem tab })
            return;

        if (e.Key == Key.Enter)
        {
            tab.CommitRenameCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            tab.CancelRenameCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is StyledElement { DataContext: ViewTabItem { IsRenaming: true } tab })
            tab.CommitRenameCommand.Execute(null);
    }

    private void OnConfirmViewDelete(object? sender, RoutedEventArgs e) => _ = Model?.ConfirmDeleteViewAsync();

    private void OnKeepView(object? sender, RoutedEventArgs e) => Model?.CancelDeleteView();

    // Glisser un onglet sur un autre le déplace à sa place. Le clic simple reste un clic : seul un geste de plus de quelques pixels réordonne.

    private void OnTabsPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressedTab = null;
        _reordering = false;
        if (!e.GetCurrentPoint(ViewTabList).Properties.IsLeftButtonPressed || e.Source is not Visual source)
            return;
        // Le bouton « ⋯ » et le champ du nom gardent leurs clics.
        if (source.FindAncestorOfType<Button>(includeSelf: true) is not null || source.FindAncestorOfType<TextBox>(includeSelf: true) is not null)
            return;

        _pressedTab = source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as ViewTabItem;
        _pressedAt = e.GetPosition(ViewTabList);
    }

    private void OnTabsMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedTab is null || !e.GetCurrentPoint(ViewTabList).Properties.IsLeftButtonPressed)
            return;

        if (!_reordering)
        {
            var moved = e.GetPosition(ViewTabList) - _pressedAt;
            if (Math.Abs(moved.X) < DragThreshold)
                return;
            _reordering = true;
            e.Pointer.Capture(ViewTabList);
            ViewTabList.Cursor = new Cursor(StandardCursorType.SizeAll);
        }
    }

    private void OnTabsReleased(object? sender, PointerReleasedEventArgs e)
    {
        var tab = _pressedTab;
        var reordering = _reordering;
        _pressedTab = null;
        _reordering = false;
        if (!reordering)
            return;

        e.Pointer.Capture(null);
        ViewTabList.Cursor = null;
        if (tab is null || Model is not { } model)
            return;

        var target = (ViewTabList.InputHitTest(e.GetPosition(ViewTabList)) as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as ViewTabItem;
        if (target is null || ReferenceEquals(target, tab))
            return;

        _ = model.MoveViewAsync(model.ViewTabs.IndexOf(tab), model.ViewTabs.IndexOf(target));
    }

    // ---- Le nom de la case ----

    private void OnCaseNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        _ = Model?.CommitCaseNameAsync();
        e.Handled = true;
    }

    private void OnCaseNameLostFocus(object? sender, RoutedEventArgs e) => _ = Model?.CommitCaseNameAsync();
}

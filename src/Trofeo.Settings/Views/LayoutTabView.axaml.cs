using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Trofeo.Settings.ViewModels;

namespace Trofeo.Settings.Views;

public partial class LayoutTabView : UserControl
{
    public LayoutTabView() => InitializeComponent();

    private LayoutTabViewModel? Model => DataContext as LayoutTabViewModel;

    /// <summary>
    /// Un type a été choisi dans la liste « Ajouter un module » : la liste se referme, mais seulement après la commande qui ajoute
    /// le module. Le bouton lève Click avant d'exécuter sa commande ; refermée tout de suite, la liste détacherait le bouton, sa
    /// commande (liée à son contexte) tomberait à null et le clic n'ajouterait rien.
    /// </summary>
    private void OnAddTypeClicked(object? sender, RoutedEventArgs e) => Dispatcher.UIThread.Post(() => AddButton.Flyout?.Hide());
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

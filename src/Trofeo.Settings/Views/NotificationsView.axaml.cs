using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Trofeo.Settings.ViewModels;

namespace Trofeo.Settings.Views;

public partial class NotificationsView : UserControl
{
    public NotificationsView() => InitializeComponent();

    private NotificationsViewModel? Model => DataContext as NotificationsViewModel;

    // Le lien d'une notification : le panneau se ferme et la fenêtre ouvre l'onglet (elle écoute Navigate).
    private void OnLinkClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not NotificationItem item)
            return;
        Model?.Open(item);
        Dispatcher.UIThread.Post(() => BellButton.Flyout?.Hide());
    }

    // La croix : la notification est supprimée, le panneau reste ouvert (la liste suivante, ou « Rien de nouveau. »).
    private void OnDismissClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is NotificationItem item)
            Model?.Dismiss(item.Key);
    }
}

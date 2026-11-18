using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels;

namespace Trofeo.Settings.Views;

public partial class SettingsTabView : UserControl
{
    public SettingsTabView()
    {
        InitializeComponent();

        // Le plafond d'images ne s'enregistre qu'au lâcher (souris ou doigt, fin du glissement de la poignée, touche relâchée) :
        // un seul pas d'annulation, et pas un rechargement du service par cran de glissement. Ces événements sont parfois
        // déjà traités par le curseur lui-même : on les écoute quand même.
        FpsSlider.AddHandler(PointerReleasedEvent, OnFpsReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        FpsSlider.AddHandler(Thumb.DragCompletedEvent, OnFpsReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        FpsSlider.AddHandler(KeyUpEvent, OnFpsReleased, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Le sélecteur de fichier est celui de la fenêtre qui porte l'onglet.
        StorageFilePicker.EnsureFor(this, DataContext as SettingsTabViewModel);
        Watch(DataContext as SettingsTabViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Watch(null);
    }

    private SettingsTabViewModel? _watched;

    // La cloche des notifications demande la section « Modules complémentaires » : elle vient à l'écran. Une demande faite avant que la
    // vue existe (l'onglet vient de s'ouvrir) est prise dès l'attache.
    private void Watch(SettingsTabViewModel? model)
    {
        if (ReferenceEquals(_watched, model))
            return;
        if (_watched is not null)
            _watched.CommunityViewRequested -= BringCommunityIntoView;
        _watched = model;
        if (model is null)
            return;
        model.CommunityViewRequested += BringCommunityIntoView;
        BringCommunityIntoView();
    }

    private void BringCommunityIntoView()
    {
        if (_watched?.TakeCommunityViewRequest() != true)
            return;
        // Après la mise en page : le panneau n'a pas encore sa place quand l'onglet vient de s'ouvrir.
        Dispatcher.UIThread.Post(() => CommunityPanel.BringIntoView(), DispatcherPriority.Loaded);
    }

    private void OnFpsReleased(object? sender, RoutedEventArgs e) =>
        _ = (DataContext as SettingsTabViewModel)?.CommitMaxFpsAsync();
}

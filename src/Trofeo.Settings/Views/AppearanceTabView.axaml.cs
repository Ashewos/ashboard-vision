using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels;

namespace Trofeo.Settings.Views;

public partial class AppearanceTabView : UserControl
{
    public AppearanceTabView()
    {
        InitializeComponent();

        // La transparence ne s'enregistre qu'au lâcher (souris ou doigt, fin du glissement de la poignée, touche relâchée) :
        // un seul pas d'annulation, et pas un rechargement du service par pixel de glissement. Ces événements sont parfois
        // déjà traités par le curseur lui-même : on les écoute quand même.
        OpacitySlider.AddHandler(PointerReleasedEvent, OnOpacityReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        OpacitySlider.AddHandler(Thumb.DragCompletedEvent, OnOpacityReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        OpacitySlider.AddHandler(KeyUpEvent, OnOpacityReleased, RoutingStrategies.Bubble, handledEventsToo: true);

        // Les curseurs du verre : même règle que la transparence, un seul enregistrement au lâcher (un lâcher sans brouillon n'écrit rien).
        GlassSection.AddHandler(PointerReleasedEvent, OnGlassReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        GlassSection.AddHandler(Thumb.DragCompletedEvent, OnGlassReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        GlassSection.AddHandler(KeyUpEvent, OnGlassReleased, RoutingStrategies.Bubble, handledEventsToo: true);

        // La liste des polices : les flèches (et la frappe) ne font que déplacer la sélection ; la police ne s'enregistre qu'au
        // clic (ou au toucher) sur une ligne, ou à Entrée. Sinon chaque flèche réécrirait le fichier et rechargerait le service.
        FontList.AddHandler(PointerReleasedEvent, OnFontClicked, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        FontList.AddHandler(KeyDownEvent, OnFontKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);

        // Un champ de couleur quitté est validé comme avec Entrée.
        HexBox.LostFocus += (_, _) => (DataContext as AppearanceTabViewModel)?.ApplyColorTextCommand.Execute(null);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Le sélecteur de fichier est celui de la fenêtre qui porte l'onglet.
        if (DataContext is AppearanceTabViewModel { Picker: null } model && TopLevel.GetTopLevel(this) is { } top)
        {
            model.Picker = new StorageFilePicker(top);
        }
    }

    private void OnFontClicked(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || DataContext is not AppearanceTabViewModel model)
            return;
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is FontItem item)
            _ = model.SetFontAsync(item);
    }

    private void OnFontKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is AppearanceTabViewModel model && FontList.SelectedItem is FontItem item)
            _ = model.SetFontAsync(item);
    }

    private void OnGlassReleased(object? sender, RoutedEventArgs e) =>
        _ = (DataContext as AppearanceTabViewModel)?.Glass.CommitAsync();

    private void OnOpacityReleased(object? sender, RoutedEventArgs e) =>
        _ = (DataContext as AppearanceTabViewModel)?.CommitOpacityAsync();
}

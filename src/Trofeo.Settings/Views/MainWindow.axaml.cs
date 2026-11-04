using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Trofeo.Settings.ViewModels;

namespace Trofeo.Settings.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Une ligne de l'onglet Raccourcis attend sa combinaison : toutes les touches vont à la capture, avant n'importe quel
        // contrôle (Ctrl+Z, Tab, Entrée) et avant les raccourcis de la fenêtre. Écouté en descendant, donc quel que soit le focus.
        AddHandler(KeyDownEvent, OnCaptureKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnCaptureKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel { ShortcutsTab.IsCapturing: true } model)
            return;
        e.Handled = true;
        _ = model.ShortcutsTab.CaptureAsync(e.Key, e.KeyModifiers);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Réduite ou cachée, la fenêtre ne montre rien : l'aperçu se met en pause, et la consommation ne se mesure plus.
        if ((change.Property == WindowStateProperty || change.Property == IsVisibleProperty) && DataContext is MainViewModel model)
            model.SetPreviewPaused(WindowState == WindowState.Minimized || !IsVisible);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || DataContext is not MainViewModel model)
            return;

        // Échap ferme la boîte d'un module complémentaire (une mise à jour en cours est abandonnée).
        if (e.Key == Key.Escape && model.SettingsTab.Community.Dialog is { } dialog)
        {
            _ = dialog.CancelAsync();
            e.Handled = true;
            return;
        }

        // Échap abandonne un glissement du damier.
        if (e.Key == Key.Escape && model.LayoutTab.IsDragging)
        {
            model.LayoutTab.CancelDrag();
            e.Handled = true;
            return;
        }

        // Ctrl+Z annule, Ctrl+Y (ou Ctrl+Maj+Z) rétablit : la dernière modification, de quelque onglet qu'elle vienne.
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || model.LayoutTab.IsDragging)
            return;
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (e.Key == Key.Z && !shift)
        {
            _ = model.LayoutTab.UndoAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Y || e.Key == Key.Z && shift)
        {
            _ = model.LayoutTab.RedoAsync();
            e.Handled = true;
        }
    }

    /// <summary>Ramène la fenêtre au premier plan (un second lancement l'a demandé).</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }
}

using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Trofeo.Settings.ViewModels;

namespace Trofeo.Settings.Views;

/// <summary>Le titre de la case choisie, avec son crayon : la saisie prend le focus, Entrée la valide, Échap l'abandonne.</summary>
public partial class CaseNameHeader : UserControl
{
    private CaseNameEditor? _model;

    public CaseNameHeader() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
            _model.PropertyChanged -= OnModelChanged;
        _model = DataContext as CaseNameEditor;
        if (_model is not null)
            _model.PropertyChanged += OnModelChanged;
    }

    // Le crayon vient d'ouvrir la saisie : le champ prend le focus, son texte tout sélectionné (taper remplace le nom).
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CaseNameEditor.IsRenaming) || _model is not { IsRenaming: true })
            return;
        Dispatcher.UIThread.Post(() =>
        {
            CaseNameBox.Focus();
            CaseNameBox.SelectAll();
        });
    }

    private void OnNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (_model is null)
            return;
        if (e.Key == Key.Enter)
        {
            _ = _model.CommitAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _model.Cancel();
            e.Handled = true;
        }
    }

    private void OnNameLostFocus(object? sender, RoutedEventArgs e) => _ = _model?.CommitAsync();
}

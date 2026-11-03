using Avalonia.Controls;
using Avalonia.Input;
using Trofeo.Settings.ViewModels.Fields;

namespace Trofeo.Settings.Views.Fields;

public partial class NumberFieldView : UserControl
{
    public NumberFieldView()
    {
        InitializeComponent();

        // Comme un texte : le nombre ne s'enregistre qu'à la perte du focus ou sur Entrée.
        Box.LostFocus += (_, _) => Commit();
        Box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
        };
    }

    private void Commit()
    {
        if (DataContext is NumberFieldViewModel { IsDirty: true } field)
            _ = field.CommitAsync();
    }
}

namespace AutoUnlockManager.Views.Controls;

/// <summary>Campo numérico con la unidad a la derecha («条», «秒», «dBm»). Plantilla en Themes/Controls.xaml.</summary>
public sealed class UnitTextBox : System.Windows.Controls.TextBox
{
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(UnitTextBox), new PropertyMetadata(""));

    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }
}

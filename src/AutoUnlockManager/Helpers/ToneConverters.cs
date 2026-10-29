using System.Globalization;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace AutoUnlockManager.Helpers;

/// <summary>
/// Tono de estado → icono. El estado nunca depende solo del color (UiDesign.md 2.5):
/// cada tono tiene su forma — ok ✓, warn ⚠, danger ✕, info ◐, neutral ○.
/// </summary>
public sealed class ToneToSymbolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => (value as string) switch
    {
        "ok" => SymbolRegular.Checkmark12,
        "warn" => SymbolRegular.Warning12,
        "danger" => SymbolRegular.Dismiss12,
        "info" => SymbolRegular.CircleHalfFill12,
        _ => SymbolRegular.Circle12,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>null → Collapsed; cualquier otro valor → Visible.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

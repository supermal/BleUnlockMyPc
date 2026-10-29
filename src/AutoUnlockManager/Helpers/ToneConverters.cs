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

/// <summary>Conjunto de campos modificados + nombre del campo (ConverterParameter) → bool.</summary>
public sealed class FieldModifiedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IReadOnlySet<string> fields && parameter is string field && fields.Contains(field);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Igual que <see cref="FieldModifiedConverter"/> pero devuelve Visibility.</summary>
public sealed class FieldModifiedToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IReadOnlySet<string> fields && parameter is string field && fields.Contains(field)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>true → Collapsed; false → Visible.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>MultiBinding: true solo si todos los valores son true.</summary>
public sealed class AllTrueConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.All(v => v is true);

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

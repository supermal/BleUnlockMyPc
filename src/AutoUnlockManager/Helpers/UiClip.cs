using System.Windows.Controls;
using System.Windows.Media;

namespace AutoUnlockManager.Helpers;

/// <summary>
/// Recorta el hijo de un Border según su CornerRadius (UiDesign.md, sección 8.7).
/// Border.CornerRadius no recorta a sus hijos por sí solo.
/// </summary>
public static class UiClip
{
    public static readonly DependencyProperty ToCornerRadiusProperty =
        DependencyProperty.RegisterAttached(
            "ToCornerRadius",
            typeof(bool),
            typeof(UiClip),
            new PropertyMetadata(false, OnChanged));

    public static void SetToCornerRadius(Border border, bool value) => border.SetValue(ToCornerRadiusProperty, value);

    public static bool GetToCornerRadius(Border border) => (bool)border.GetValue(ToCornerRadiusProperty);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border)
        {
            return;
        }

        border.SizeChanged -= Update;

        if ((bool)e.NewValue)
        {
            border.SizeChanged += Update;
        }
        else if (border.Child is UIElement child)
        {
            child.Clip = null;
        }
    }

    private static void Update(object sender, SizeChangedEventArgs e)
    {
        var border = (Border)sender;
        if (border.Child is not FrameworkElement child)
        {
            return;
        }

        // El radio interior descuenta el grosor del borde
        var radius = Math.Max(0, border.CornerRadius.TopLeft - border.BorderThickness.Left);
        child.Clip = new RectangleGeometry(new Rect(0, 0, child.ActualWidth, child.ActualHeight), radius, radius);
    }
}

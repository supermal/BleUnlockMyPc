using System.Globalization;
using System.Windows.Documents;
using System.Windows.Media;
using BleUnlockManager.Core.Localization;
using BleUnlockManager.Core.Resources;

namespace BleUnlockManager.Views.Controls;

/// <summary>
/// Banda de umbrales del formulario: lejos / mantener / cerca entre -100 y -30 dBm, con marcas
/// sobre los dos umbrales. Mismo lenguaje visual que las zonas de la curva de la vista general.
/// </summary>
public sealed class ThresholdBar : FrameworkElement
{
    private const double MinDbm = -100;
    private const double MaxDbm = -30;
    private const double BarTop = 30;
    private const double BarHeight = 8;

    public static readonly DependencyProperty LockDbmProperty = Register(nameof(LockDbm), typeof(double), -80.0);
    public static readonly DependencyProperty UnlockDbmProperty = Register(nameof(UnlockDbm), typeof(double), -65.0);
    public static readonly DependencyProperty AwayBrushProperty = Register(nameof(AwayBrush), typeof(Brush), Brushes.MistyRose);
    public static readonly DependencyProperty HoldBrushProperty = Register(nameof(HoldBrush), typeof(Brush), Brushes.Gainsboro);
    public static readonly DependencyProperty NearBrushProperty = Register(nameof(NearBrush), typeof(Brush), Brushes.Honeydew);
    public static readonly DependencyProperty AwayTextBrushProperty = Register(nameof(AwayTextBrush), typeof(Brush), Brushes.Firebrick);
    public static readonly DependencyProperty NearTextBrushProperty = Register(nameof(NearTextBrush), typeof(Brush), Brushes.Green);
    public static readonly DependencyProperty MarkBrushProperty = Register(nameof(MarkBrush), typeof(Brush), Brushes.Black);
    public static readonly DependencyProperty LabelBrushProperty = Register(nameof(LabelBrush), typeof(Brush), Brushes.Gray);

    public double LockDbm { get => (double)GetValue(LockDbmProperty); set => SetValue(LockDbmProperty, value); }

    public double UnlockDbm { get => (double)GetValue(UnlockDbmProperty); set => SetValue(UnlockDbmProperty, value); }

    public Brush AwayBrush { get => (Brush)GetValue(AwayBrushProperty); set => SetValue(AwayBrushProperty, value); }

    public Brush HoldBrush { get => (Brush)GetValue(HoldBrushProperty); set => SetValue(HoldBrushProperty, value); }

    public Brush NearBrush { get => (Brush)GetValue(NearBrushProperty); set => SetValue(NearBrushProperty, value); }

    public Brush AwayTextBrush { get => (Brush)GetValue(AwayTextBrushProperty); set => SetValue(AwayTextBrushProperty, value); }

    public Brush NearTextBrush { get => (Brush)GetValue(NearTextBrushProperty); set => SetValue(NearTextBrushProperty, value); }

    public Brush MarkBrush { get => (Brush)GetValue(MarkBrushProperty); set => SetValue(MarkBrushProperty, value); }

    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(0, 76);

    public ThresholdBar()
    {
        // Los textos se dibujan en OnRender: al cambiar el idioma basta con volver a dibujar
        // Al volver a mostrarse se redibuja, por si el idioma cambió mientras estaba en otra página
        Loaded += (_, _) =>
        {
            ManagerLanguage.Changed += OnLanguageChanged;
            InvalidateVisual();
        };
        Unloaded += (_, _) => ManagerLanguage.Changed -= OnLanguageChanged;
    }

    private void OnLanguageChanged() => Dispatcher.BeginInvoke(InvalidateVisual);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        if (width < 40)
        {
            return;
        }

        double X(double dbm) => (Math.Clamp(dbm, MinDbm, MaxDbm) - MinDbm) / (MaxDbm - MinDbm) * width;

        // Umbrales incoherentes (desbloqueo por debajo del bloqueo) se dibujan sin cruzarse
        var lockX = X(LockDbm);
        var unlockX = Math.Max(lockX, X(UnlockDbm));

        dc.PushClip(new RectangleGeometry(new Rect(0, BarTop, width, BarHeight), BarHeight / 2, BarHeight / 2));
        dc.DrawRectangle(AwayBrush, null, new Rect(0, BarTop, lockX, BarHeight));
        dc.DrawRectangle(HoldBrush, null, new Rect(lockX, BarTop, unlockX - lockX, BarHeight));
        dc.DrawRectangle(NearBrush, null, new Rect(unlockX, BarTop, width - unlockX, BarHeight));
        dc.Pop();

        DrawMark(dc, lockX, string.Format(UiStrings.Bar_Lock, Whole(LockDbm)), AwayTextBrush);
        DrawMark(dc, unlockX, string.Format(UiStrings.Bar_Unlock, Whole(UnlockDbm)), NearTextBrush);

        var zoneY = BarTop + BarHeight + 6;
        DrawText(dc, UiStrings.Zone_Away, LabelBrush, new Point(0, zoneY));
        DrawText(dc, UiStrings.Zone_Hold, LabelBrush, new Point(lockX + 4, zoneY));
        DrawText(dc, UiStrings.Zone_Near, LabelBrush, new Point(unlockX + 4, zoneY));

        var scaleY = zoneY + 18;
        DrawText(dc, "-100 dBm", LabelBrush, new Point(0, scaleY));
        var right = Format("-30 dBm", LabelBrush);
        dc.DrawText(right, new Point(width - right.Width, scaleY));
    }

    private void DrawMark(DrawingContext dc, double x, string label, Brush brush)
    {
        dc.DrawRoundedRectangle(MarkBrush, null, new Rect(x - 1, BarTop - 4, 2, BarHeight + 8), 1, 1);
        var text = Format(label, brush);
        var left = Math.Clamp(x - text.Width / 2, 0, Math.Max(0, ActualWidth - text.Width));
        dc.DrawText(text, new Point(left, BarTop - 8 - text.Height));
    }

    private void DrawText(DrawingContext dc, string text, Brush brush, Point at) => dc.DrawText(Format(text, brush), at);

    private FormattedText Format(string text, Brush brush) => new(
        text,
        CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight,
        new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
        12,
        brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static string Whole(double value) => value.ToString("0", CultureInfo.InvariantCulture);

    private static DependencyProperty Register(string name, Type type, object defaultValue) =>
        DependencyProperty.Register(name, type, typeof(ThresholdBar),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));
}

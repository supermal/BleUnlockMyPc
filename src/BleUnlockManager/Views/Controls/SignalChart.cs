using System.Globalization;
using System.Windows.Documents;
using System.Windows.Media;
using BleUnlockManager.Core.Localization;
using BleUnlockManager.Core.Management;
using BleUnlockManager.Core.Resources;

namespace BleUnlockManager.Views.Controls;

/// <summary>
/// Curva de señal con bandas de zona (cerca / lejos) y umbrales discontinuos. Eje Y fijo
/// -100…-30 dBm. Los pinceles son propiedades de dependencia para enlazarlos con DynamicResource:
/// al cambiar de tema se vuelve a dibujar sin código adicional.
/// </summary>
public sealed class SignalChart : FrameworkElement
{
    private const double TopDbm = -30;
    private const double BottomDbm = -100;
    private const double LabelColumn = 44;
    private const double AxisRow = 22;

    public static readonly DependencyProperty PointsProperty = Register(nameof(Points), typeof(IReadOnlyList<SamplePoint>), null);
    public static readonly DependencyProperty LockDbmProperty = Register(nameof(LockDbm), typeof(double), double.NaN);
    public static readonly DependencyProperty UnlockDbmProperty = Register(nameof(UnlockDbm), typeof(double), double.NaN);
    public static readonly DependencyProperty NowMsProperty = Register(nameof(NowMs), typeof(long), 0L);
    public static readonly DependencyProperty WindowSecondsProperty = Register(nameof(WindowSeconds), typeof(double), 300.0);
    public static readonly DependencyProperty ShowAxisLabelsProperty = Register(nameof(ShowAxisLabels), typeof(bool), true);
    public static readonly DependencyProperty LineBrushProperty = Register(nameof(LineBrush), typeof(Brush), Brushes.SteelBlue);
    public static readonly DependencyProperty NearBrushProperty = Register(nameof(NearBrush), typeof(Brush), Brushes.Green);
    public static readonly DependencyProperty AwayBrushProperty = Register(nameof(AwayBrush), typeof(Brush), Brushes.Firebrick);
    public static readonly DependencyProperty LabelBrushProperty = Register(nameof(LabelBrush), typeof(Brush), Brushes.Gray);
    public static readonly DependencyProperty AxisBrushProperty = Register(nameof(AxisBrush), typeof(Brush), Brushes.LightGray);
    public static readonly DependencyProperty SurfaceBrushProperty = Register(nameof(SurfaceBrush), typeof(Brush), Brushes.White);

    public IReadOnlyList<SamplePoint>? Points { get => (IReadOnlyList<SamplePoint>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }

    public double LockDbm { get => (double)GetValue(LockDbmProperty); set => SetValue(LockDbmProperty, value); }

    public double UnlockDbm { get => (double)GetValue(UnlockDbmProperty); set => SetValue(UnlockDbmProperty, value); }

    /// <summary>Hora actual del servicio (ms UTC): el borde derecho de la ventana.</summary>
    public long NowMs { get => (long)GetValue(NowMsProperty); set => SetValue(NowMsProperty, value); }

    public double WindowSeconds { get => (double)GetValue(WindowSecondsProperty); set => SetValue(WindowSecondsProperty, value); }

    /// <summary>false en la bandeja: sin etiquetas de ejes ni de zona.</summary>
    public bool ShowAxisLabels { get => (bool)GetValue(ShowAxisLabelsProperty); set => SetValue(ShowAxisLabelsProperty, value); }

    public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }

    public Brush NearBrush { get => (Brush)GetValue(NearBrushProperty); set => SetValue(NearBrushProperty, value); }

    public Brush AwayBrush { get => (Brush)GetValue(AwayBrushProperty); set => SetValue(AwayBrushProperty, value); }

    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

    public Brush AxisBrush { get => (Brush)GetValue(AxisBrushProperty); set => SetValue(AxisBrushProperty, value); }

    /// <summary>Fondo de la tarjeta: aro del punto final.</summary>
    public Brush SurfaceBrush { get => (Brush)GetValue(SurfaceBrushProperty); set => SetValue(SurfaceBrushProperty, value); }

    public SignalChart()
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
        var labels = ShowAxisLabels;
        var plot = new Rect(
            labels ? LabelColumn : 0,
            0,
            Math.Max(0, ActualWidth - (labels ? LabelColumn : 0)),
            Math.Max(0, ActualHeight - (labels ? AxisRow : 0)));
        if (plot.Width < 2 || plot.Height < 2)
        {
            return;
        }

        // Con el dispositivo pegado al equipo la señal supera -30 dBm: recortada al borde la curva
        // sería una recta inmóvil, así que el techo sube a la decena que la contenga (máx. 0 dBm)
        var top = TopDbm;
        if (Points is { Count: > 0 } visible)
        {
            var max = visible.Max(static p => p.Rssi);
            if (max > top)
            {
                top = Math.Min(0, Math.Ceiling(max / 10) * 10);
            }
        }

        double Y(double dbm) => plot.Top + (top - Math.Clamp(dbm, BottomDbm, top)) / (top - BottomDbm) * plot.Height;

        // Fondo transparente para que toda el área responda a la herramienta de accesibilidad
        dc.DrawRectangle(Brushes.Transparent, null, plot);

        var hasThresholds = !double.IsNaN(LockDbm) && !double.IsNaN(UnlockDbm);
        if (hasThresholds)
        {
            var nearY = Y(UnlockDbm);
            var awayY = Y(LockDbm);
            dc.DrawRectangle(WithOpacity(NearBrush, 0.08), null, new Rect(plot.Left, plot.Top, plot.Width, nearY - plot.Top));
            dc.DrawRectangle(WithOpacity(AwayBrush, 0.07), null, new Rect(plot.Left, awayY, plot.Width, plot.Bottom - awayY));
            DrawDashed(dc, NearBrush, plot, nearY);
            DrawDashed(dc, AwayBrush, plot, awayY);
        }

        dc.DrawLine(new Pen(AxisBrush, 1), new Point(plot.Left, plot.Bottom - 0.5), new Point(plot.Right, plot.Bottom - 0.5));

        if (labels)
        {
            DrawYLabel(dc, plot, Y(top), Whole(top), LabelBrush);
            DrawYLabel(dc, plot, Y(BottomDbm), "-100", LabelBrush);
            if (hasThresholds)
            {
                DrawYLabel(dc, plot, Y(UnlockDbm), Whole(UnlockDbm), NearBrush);
                DrawYLabel(dc, plot, Y(LockDbm), Whole(LockDbm), AwayBrush);
                DrawText(dc, UiStrings.Chart_NearZone, NearBrush, new Point(plot.Right - 8, plot.Top + 4), alignRight: true);
                DrawText(dc, UiStrings.Chart_AwayZone, AwayBrush, new Point(plot.Right - 8, plot.Bottom - 20), alignRight: true);
            }

            var start = WindowSeconds >= 120 ? string.Format(UiStrings.Chart_MinutesAgo, WindowSeconds / 60) : string.Format(UiStrings.Chart_SecondsAgo, WindowSeconds);
            DrawText(dc, start, LabelBrush, new Point(plot.Left, plot.Bottom + 4), alignRight: false);
            DrawText(dc, UiStrings.Chart_Now, LabelBrush, new Point(plot.Right, plot.Bottom + 4), alignRight: true);
        }

        var points = Points;
        if (points is null || points.Count == 0 || NowMs <= 0)
        {
            if (labels)
            {
                var empty = Format(UiStrings.Chart_WaitingSamples, LabelBrush, 13);
                dc.DrawText(empty, new Point(plot.Left + (plot.Width - empty.Width) / 2, plot.Top + (plot.Height - empty.Height) / 2));
            }

            return;
        }

        var windowMs = WindowSeconds * 1000;
        var startMs = NowMs - windowMs;
        double X(long ts) => plot.Left + Math.Clamp((ts - startMs) / windowMs, 0, 1) * plot.Width;

        var geometry = new StreamGeometry();
        Point last = default;
        using (var ctx = geometry.Open())
        {
            var first = true;
            foreach (var p in points)
            {
                if (p.TsMs < startMs)
                {
                    continue;
                }

                last = new Point(X(p.TsMs), Y(p.Rssi));
                if (first)
                {
                    ctx.BeginFigure(last, isFilled: false, isClosed: false);
                    first = false;
                }
                else
                {
                    ctx.LineTo(last, isStroked: true, isSmoothJoin: true);
                }
            }

            if (first)
            {
                return;
            }
        }

        geometry.Freeze();
        dc.PushClip(new RectangleGeometry(new Rect(plot.Left, plot.Top - 6, plot.Width + 6, plot.Height + 12)));
        dc.DrawGeometry(null, new Pen(LineBrush, 1.75) { LineJoin = PenLineJoin.Round }, geometry);
        dc.DrawEllipse(LineBrush, new Pen(SurfaceBrush, 2), last, 4, 4);
        dc.Pop();
    }

    private static DependencyProperty Register(string name, Type type, object? defaultValue) =>
        DependencyProperty.Register(name, type, typeof(SignalChart),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));

    private static Brush WithOpacity(Brush brush, double opacity)
    {
        var copy = brush.CloneCurrentValue();
        copy.Opacity = opacity;
        copy.Freeze();
        return copy;
    }

    private static void DrawDashed(DrawingContext dc, Brush brush, Rect plot, double y)
    {
        var pen = new Pen(WithOpacity(brush, 0.7), 1) { DashStyle = new DashStyle([4, 4], 0) };
        dc.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));
    }

    private void DrawYLabel(DrawingContext dc, Rect plot, double y, string text, Brush brush)
    {
        var formatted = Format(text, brush, 12);
        dc.DrawText(formatted, new Point(plot.Left - 6 - formatted.Width, y - formatted.Height / 2));
    }

    private void DrawText(DrawingContext dc, string text, Brush brush, Point anchor, bool alignRight)
    {
        var formatted = Format(text, brush, 12);
        dc.DrawText(formatted, alignRight ? new Point(anchor.X - formatted.Width, anchor.Y) : anchor);
    }

    private FormattedText Format(string text, Brush brush, double size) => new(
        text,
        CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight,
        new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
        size,
        brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static string Whole(double value) => value.ToString("0", CultureInfo.InvariantCulture);
}

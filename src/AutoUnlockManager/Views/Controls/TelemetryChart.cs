using System.Globalization;
using AutoUnlockManager.Core.Management;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace AutoUnlockManager.Views.Controls;

/// <summary>
/// 轻量自绘信号曲线（设计 §5.3）：5 分钟滚动窗口、双阈值线（锁定=红/解锁=绿，随 thr.* 变化）、
/// 自动量程（样本+阈值±4dB，最小跨度 12dB）、降采样由 SampleHistory 预先完成（≤600 点）。
/// 颜色每次重绘按 <see cref="FrameworkElement.ActualTheme"/> 生成（主题切换经 ActualThemeChanged 自动生效），
/// 不依赖任何 XAML 主题资源查找。
/// </summary>
public sealed class TelemetryChart : UserControl
{
    /// <summary>滚动窗口（与 SampleHistory 保留窗口一致）。</summary>
    private static readonly TimeSpan DisplayWindow = TimeSpan.FromMinutes(5);

    private const double MarginLeft = 52;   // dBm 轴标签
    private const double MarginRight = 14;
    private const double MarginTop = 12;
    private const double MarginBottom = 22; // 时间说明
    private const int GridLineCount = 5;

    private readonly Canvas _canvas;
    private readonly Polyline _trace;
    private readonly Line _lockLine;
    private readonly Line _unlockLine;
    private readonly TextBlock _lockLabel;
    private readonly TextBlock _unlockLabel;
    private readonly TextBlock _emptyText;
    private readonly TextBlock _windowStartLabel;
    private readonly TextBlock _windowEndLabel;
    private readonly List<Line> _gridLines = [];
    private readonly List<TextBlock> _gridLabels = [];

    private IReadOnlyList<SamplePoint> _points = [];
    private SnapshotThresholds? _thresholds;
    private long _nowMs;

    public TelemetryChart()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;

        _trace = new Polyline { StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
        _lockLine = new Line { StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 3 } };
        _unlockLine = new Line { StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 3 } };
        _lockLabel = new TextBlock { FontSize = 11 };
        _unlockLabel = new TextBlock { FontSize = 11 };
        _emptyText = new TextBlock { FontSize = 13, Text = "等待样本…" };
        _windowStartLabel = new TextBlock { FontSize = 11, Text = "5 分钟前" };
        _windowEndLabel = new TextBlock { FontSize = 11, Text = "现在" };

        _canvas = new Canvas
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        for (var i = 0; i < GridLineCount; i++)
        {
            var line = new Line { StrokeThickness = 1 };
            var label = new TextBlock { FontSize = 11 };
            _gridLines.Add(line);
            _gridLabels.Add(label);
            _canvas.Children.Add(line);
            _canvas.Children.Add(label);
        }

        _canvas.Children.Add(_lockLine);
        _canvas.Children.Add(_unlockLine);
        _canvas.Children.Add(_lockLabel);
        _canvas.Children.Add(_unlockLabel);
        _canvas.Children.Add(_trace);
        _canvas.Children.Add(_emptyText);
        _canvas.Children.Add(_windowStartLabel);
        _canvas.Children.Add(_windowEndLabel);

        Content = _canvas;

        SizeChanged += (_, _) => Redraw();
        ActualThemeChanged += (_, _) => Redraw();
    }

    private bool IsDark => ActualTheme == ElementTheme.Dark;

    /// <summary>更新数据（UI 线程）。points 已降采样且按时间升序；thresholds 为空 = 不画阈值线。</summary>
    public void Update(IReadOnlyList<SamplePoint> points, SnapshotThresholds? thresholds, long nowMs)
    {
        _points = points;
        _thresholds = thresholds;
        _nowMs = nowMs;
        Redraw();
    }

    private void Redraw()
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 80 || height < 60)
        {
            return; // 尚未布局
        }

        var plotLeft = MarginLeft;
        var plotRight = width - MarginRight;
        var plotTop = MarginTop;
        var plotBottom = height - MarginBottom;
        var plotWidth = plotRight - plotLeft;
        var plotHeight = plotBottom - plotTop;

        var windowStartMs = _nowMs - (long)DisplayWindow.TotalMilliseconds;

        // 窗口内样本（已按时间升序；跳过早于窗口的旧点）
        var firstVisible = 0;
        while (firstVisible < _points.Count && _points[firstVisible].TsMs < windowStartMs)
        {
            firstVisible++;
        }

        if (_points.Count - firstVisible <= 0)
        {
            ShowEmptyState(width, height);
            return;
        }

        // 自动量程：样本 + 阈值，±4dB 填充，最小跨度 12dB
        var min = double.MaxValue;
        var max = double.MinValue;
        for (var i = firstVisible; i < _points.Count; i++)
        {
            min = Math.Min(min, _points[i].Rssi);
            max = Math.Max(max, _points[i].Rssi);
        }

        if (_thresholds is not null)
        {
            min = Math.Min(min, Math.Min(_thresholds.LockDbm, _thresholds.UnlockDbm));
            max = Math.Max(max, Math.Max(_thresholds.LockDbm, _thresholds.UnlockDbm));
        }

        min -= 4;
        max += 4;
        if (max - min < 12)
        {
            var middle = (min + max) / 2;
            min = middle - 6;
            max = middle + 6;
        }

        var span = max - min;

        double Y(double value) => plotTop + (max - value) / span * plotHeight;
        double X(long ts) => Math.Clamp(
            plotLeft + (ts - windowStartMs) / (double)DisplayWindow.TotalMilliseconds * plotWidth,
            plotLeft,
            plotRight);

        var gridBrush = new SolidColorBrush(IsDark
            ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x14, 0x00, 0x00, 0x00));
        var axisBrush = new SolidColorBrush(IsDark
            ? Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x9E, 0x00, 0x00, 0x00));
        var lockBrush = new SolidColorBrush(IsDark
            ? Color.FromArgb(0xFF, 0xF1, 0x70, 0x7B)
            : Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C));
        var unlockBrush = new SolidColorBrush(IsDark
            ? Color.FromArgb(0xFF, 0x6C, 0xCB, 0x5F)
            : Color.FromArgb(0xFF, 0x10, 0x7C, 0x10));

        // 网格 + dBm 轴标签
        _emptyText.Visibility = Visibility.Collapsed;
        for (var i = 0; i < GridLineCount; i++)
        {
            var fraction = i / (double)(GridLineCount - 1);
            var y = plotTop + plotHeight * fraction;
            var value = max - span * fraction;

            var line = _gridLines[i];
            line.Stroke = gridBrush;
            line.X1 = plotLeft;
            line.X2 = plotRight;
            line.Y1 = y;
            line.Y2 = y;
            line.Visibility = Visibility.Visible;

            var label = _gridLabels[i];
            label.Text = i == 0
                ? value.ToString("0", CultureInfo.InvariantCulture) + " dBm"
                : value.ToString("0", CultureInfo.InvariantCulture);
            label.Foreground = axisBrush;
            Canvas.SetLeft(label, 4);
            Canvas.SetTop(label, Math.Clamp(y - 7, 2, plotBottom - 14));
            label.Visibility = Visibility.Visible;
        }

        // 时间说明
        _windowStartLabel.Foreground = axisBrush;
        Canvas.SetLeft(_windowStartLabel, plotLeft);
        Canvas.SetTop(_windowStartLabel, plotBottom + 3);
        _windowStartLabel.Visibility = Visibility.Visible;

        _windowEndLabel.Foreground = axisBrush;
        Canvas.SetLeft(_windowEndLabel, plotRight - 24);
        Canvas.SetTop(_windowEndLabel, plotBottom + 3);
        _windowEndLabel.Visibility = Visibility.Visible;

        // 阈值线（随 thr.* 实时变化；无阈值数据时隐藏）
        if (_thresholds is not null)
        {
            DrawThreshold(_lockLine, _lockLabel, _thresholds.LockDbm, "锁定", lockBrush, Y, plotLeft, plotRight, plotTop);
            DrawThreshold(_unlockLine, _unlockLabel, _thresholds.UnlockDbm, "解锁", unlockBrush, Y, plotLeft, plotRight, plotTop);
        }
        else
        {
            _lockLine.Visibility = Visibility.Collapsed;
            _lockLabel.Visibility = Visibility.Collapsed;
            _unlockLine.Visibility = Visibility.Collapsed;
            _unlockLabel.Visibility = Visibility.Collapsed;
        }

        // 曲线
        var points = new PointCollection();
        for (var i = firstVisible; i < _points.Count; i++)
        {
            points.Add(new Point(X(_points[i].TsMs), Y(_points[i].Rssi)));
        }

        _trace.Points = points;
        _trace.Stroke = AccentBrush();
        _trace.Visibility = Visibility.Visible;
    }

    private static void DrawThreshold(
        Line line,
        TextBlock label,
        double valueDbm,
        string name,
        Brush brush,
        Func<double, double> y,
        double plotLeft,
        double plotRight,
        double plotTop)
    {
        var yPosition = y(valueDbm);

        line.Stroke = brush;
        line.X1 = plotLeft;
        line.X2 = plotRight;
        line.Y1 = yPosition;
        line.Y2 = yPosition;
        line.Visibility = Visibility.Visible;

        label.Text = $"{name} {valueDbm.ToString("0", CultureInfo.InvariantCulture)}";
        label.Foreground = brush;
        Canvas.SetLeft(label, plotLeft + 6);
        Canvas.SetTop(label, yPosition - 16 < plotTop ? yPosition + 2 : yPosition - 16);
        label.Visibility = Visibility.Visible;
    }

    private void ShowEmptyState(double width, double height)
    {
        foreach (var line in _gridLines)
        {
            line.Visibility = Visibility.Collapsed;
        }

        foreach (var label in _gridLabels)
        {
            label.Visibility = Visibility.Collapsed;
        }

        _lockLine.Visibility = Visibility.Collapsed;
        _unlockLine.Visibility = Visibility.Collapsed;
        _lockLabel.Visibility = Visibility.Collapsed;
        _unlockLabel.Visibility = Visibility.Collapsed;
        _trace.Visibility = Visibility.Collapsed;
        _windowStartLabel.Visibility = Visibility.Collapsed;
        _windowEndLabel.Visibility = Visibility.Collapsed;

        _emptyText.Foreground = new SolidColorBrush(IsDark
            ? Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x9E, 0x00, 0x00, 0x00));
        Canvas.SetLeft(_emptyText, width / 2 - 38);
        Canvas.SetTop(_emptyText, height / 2 - 10);
        _emptyText.Visibility = Visibility.Visible;
    }

    private static SolidColorBrush AccentBrush()
    {
        try
        {
            if (Application.Current.Resources.TryGetValue("SystemAccentColor", out var value) && value is Color color)
            {
                return new SolidColorBrush(color);
            }
        }
        catch
        {
            // 资源不可用 → 兜底色
        }

        return new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x78, 0xD4));
    }
}

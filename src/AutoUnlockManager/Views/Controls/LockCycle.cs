using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AutoUnlockManager.Core.Management;

namespace AutoUnlockManager.Views.Controls;

/// <summary>
/// Ciclo de bloqueo en cinco pasos; el paso actual va relleno con el color de acento y, si hay un
/// lote en curso, muestra debajo su progreso (segmentos + «离开确认 3/5»). Suspendido y pospuesto
/// no forman parte de la línea principal: con ellos ningún paso queda marcado.
/// </summary>
public sealed class LockCycle : ContentControl
{
    private static readonly (string State, string Label)[] Steps =
    [
        ("Unlocked", "已解锁"),
        ("PendingLock", "待锁定"),
        ("LockedWaitingDeparture", "已锁定 · 等待离开"),
        ("LockedArmed", "已锁定 · 监控中"),
        ("UnlockPending", "解锁确认中"),
    ];

    private const int MaxSegments = 10;

    public static readonly DependencyProperty StateNameProperty = DependencyProperty.Register(
        nameof(StateName), typeof(string), typeof(LockCycle), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty BatchProperty = DependencyProperty.Register(
        nameof(Batch), typeof(SnapshotBatch), typeof(LockCycle), new PropertyMetadata(null, OnChanged));

    public LockCycle()
    {
        Focusable = false;
        Rebuild();
    }

    public string? StateName { get => (string?)GetValue(StateNameProperty); set => SetValue(StateNameProperty, value); }

    public SnapshotBatch? Batch { get => (SnapshotBatch?)GetValue(BatchProperty); set => SetValue(BatchProperty, value); }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((LockCycle)d).Rebuild();

    private void Rebuild()
    {
        var grid = new Grid();
        var current = Array.FindIndex(Steps, s => s.State == StateName);

        for (var i = 0; i < Steps.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var step = BuildStep(Steps[i].Label, i == current, last: i == Steps.Length - 1);
            Grid.SetColumn(step, i);
            grid.Children.Add(step);
        }

        AutomationProperties.SetName(grid, current >= 0 ? $"锁定周期：当前「{Steps[current].Label}」" : "锁定周期");
        Content = grid;
    }

    private FrameworkElement BuildStep(string label, bool isCurrent, bool last)
    {
        var panel = new StackPanel();

        // Fila del punto con la línea que lo une al siguiente paso
        var dotRow = new Grid { Height = 12 };
        if (!last)
        {
            var line = new Rectangle { Height = 2, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            line.SetResourceReference(Shape.FillProperty, "Ui.Stroke.Divider");
            dotRow.Children.Add(line);
        }

        var dot = new Ellipse { Width = 12, Height = 12, StrokeThickness = 2, HorizontalAlignment = HorizontalAlignment.Left };
        if (isCurrent)
        {
            dot.SetResourceReference(Shape.FillProperty, "Ui.Primary");
            dot.SetResourceReference(Shape.StrokeProperty, "Ui.Primary");
        }
        else
        {
            dot.SetResourceReference(Shape.FillProperty, "Ui.Bg.Card");
            dot.SetResourceReference(Shape.StrokeProperty, "Ui.Stroke.Control.Strong");
        }

        dotRow.Children.Add(dot);
        panel.Children.Add(dotRow);

        var text = new TextBlock
        {
            Text = label,
            Margin = new Thickness(0, 6, 8, 0),
            FontSize = 12,
            FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, isCurrent ? "Ui.Text.Primary" : "Ui.Text.Secondary");
        panel.Children.Add(text);

        if (isCurrent && Batch is { Of: > 0 } batch)
        {
            panel.Children.Add(BuildProgress(batch));
        }

        return panel;
    }

    private static FrameworkElement BuildProgress(SnapshotBatch batch)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };

        // Lotes largos (hasta 50) se reducen a 10 segmentos proporcionales
        var segments = Math.Min(batch.Of, MaxSegments);
        var filled = batch.Of <= MaxSegments ? batch.N : (int)Math.Round(batch.N * (double)MaxSegments / batch.Of);
        for (var i = 0; i < segments; i++)
        {
            var segment = new Border { Width = 14, Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 3, 0) };
            segment.SetResourceReference(Border.BackgroundProperty, i < filled ? "Ui.Status.Warning.Fg" : "Ui.Bg.Badge");
            row.Children.Add(segment);
        }

        var text = new TextBlock
        {
            Text = $"{KindLabel(batch.Kind)} {batch.N}/{batch.Of}",
            Margin = new Thickness(5, 0, 0, 0),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Status.Warning.Fg");
        row.Children.Add(text);
        return row;
    }

    private static string KindLabel(string kind) => kind switch
    {
        "lockConfirm" => "锁屏确认",
        "unlock" => "解锁确认",
        _ => "离开确认",
    };
}

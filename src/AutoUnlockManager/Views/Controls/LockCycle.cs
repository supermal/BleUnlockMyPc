using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AutoUnlockManager.Core.Localization;
using AutoUnlockManager.Core.Management;
using AutoUnlockManager.Core.Resources;

namespace AutoUnlockManager.Views.Controls;

/// <summary>
/// Ciclo de bloqueo en cinco pasos; el paso actual va relleno con el color de acento y, si hay un
/// lote en curso, muestra debajo su progreso (segmentos + «离开确认 3/5»). Suspendido y pospuesto
/// no forman parte de la línea principal: con ellos ningún paso queda marcado.
/// </summary>
public sealed class LockCycle : ContentControl
{
    // Propiedad, no campo: las etiquetas se leen en el idioma vigente en cada reconstrucción
    private static (string State, string Label)[] Steps =>
    [
        ("Unlocked", UiStrings.Cycle_Unlocked),
        ("PendingLock", UiStrings.Cycle_PendingLock),
        ("LockedWaitingDeparture", UiStrings.Cycle_LockedWaitingDeparture),
        ("LockedArmed", UiStrings.Cycle_LockedArmed),
        ("UnlockPending", UiStrings.Cycle_UnlockPending),
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

        // Solo mientras está en pantalla: el aviso es estático y no debe retener el control. Al volver
        // a mostrarse se reconstruye, por si el idioma cambió mientras estaba en otra página
        Loaded += (_, _) =>
        {
            ManagerLanguage.Changed += OnLanguageChanged;
            Rebuild();
        };
        Unloaded += (_, _) => ManagerLanguage.Changed -= OnLanguageChanged;
    }

    private void OnLanguageChanged() => Dispatcher.BeginInvoke(Rebuild);

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
            var step = BuildStep(Steps[i].Label, i == current, first: i == 0, last: i == Steps.Length - 1);
            Grid.SetColumn(step, i);
            grid.Children.Add(step);
        }

        AutomationProperties.SetName(grid, current >= 0 ? string.Format(UiStrings.Cycle_NameCurrent, Steps[current].Label) : UiStrings.Overview_LockCycle);
        Content = grid;
    }

    // Cada paso ocupa una quinta parte del ancho con el punto centrado: la línea une los centros de
    // punto a punto (media línea a cada lado), así el ciclo llena la fila y queda simétrico
    private FrameworkElement BuildStep(string label, bool isCurrent, bool first, bool last)
    {
        var panel = new StackPanel();

        var dotRow = new Grid { Height = 12 };
        dotRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dotRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (!first)
        {
            dotRow.Children.Add(Connector(column: 0));
        }

        if (!last)
        {
            dotRow.Children.Add(Connector(column: 1));
        }

        var dot = new Ellipse { Width = 12, Height = 12, StrokeThickness = 2, HorizontalAlignment = HorizontalAlignment.Center };
        Grid.SetColumnSpan(dot, 2);
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

        // Centrado bajo el punto; los nombres largos (inglés, español) pasan a dos líneas en vez de cortarse
        var text = new TextBlock
        {
            Text = label,
            Margin = new Thickness(4, 6, 4, 0),
            FontSize = 12,
            FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 34,
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

    private static Rectangle Connector(int column)
    {
        var line = new Rectangle { Height = 2, VerticalAlignment = VerticalAlignment.Center };
        line.SetResourceReference(Shape.FillProperty, "Ui.Stroke.Divider");
        Grid.SetColumn(line, column);
        return line;
    }

    // Segmentos y texto centrados bajo el paso, uno encima del otro: en una sola fila no caben en
    // una quinta parte del ancho cuando el lote es largo o el idioma alarga el texto
    private static FrameworkElement BuildProgress(SnapshotBatch batch)
    {
        var block = new StackPanel { Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };

        // Lotes largos (hasta 50) se reducen a 10 segmentos proporcionales
        var segments = Math.Min(batch.Of, MaxSegments);
        var filled = batch.Of <= MaxSegments ? batch.N : (int)Math.Round(batch.N * (double)MaxSegments / batch.Of);
        for (var i = 0; i < segments; i++)
        {
            var segment = new Border { Width = 12, Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(i == 0 ? 0 : 3, 0, 0, 0) };
            segment.SetResourceReference(Border.BackgroundProperty, i < filled ? "Ui.Status.Warning.Fg" : "Ui.Bg.Badge");
            row.Children.Add(segment);
        }

        block.Children.Add(row);

        var text = new TextBlock
        {
            Text = $"{KindLabel(batch.Kind)} {batch.N}/{batch.Of}",
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Status.Warning.Fg");
        block.Children.Add(text);
        return block;
    }

    private static string KindLabel(string kind) => kind switch
    {
        "lockConfirm" => UiStrings.Batch_LockConfirm,
        "unlock" => UiStrings.Batch_Unlock,
        _ => UiStrings.Batch_Leave,
    };
}

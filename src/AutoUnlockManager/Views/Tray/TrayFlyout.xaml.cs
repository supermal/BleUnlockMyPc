using System.ComponentModel;
using System.Windows.Controls;
using AutoUnlockManager.Core;
using AutoUnlockManager.Core.Telemetry;
using AutoUnlockManager.Services;
using AutoUnlockManager.Core.Resources;

namespace AutoUnlockManager.Views.Tray;

/// <summary>
/// Vista rápida que abre el clic izquierdo en la bandeja. El contexto de datos es el
/// <see cref="TelemetryStore"/>; los textos derivados se recalculan solo mientras el panel es visible.
/// </summary>
public sealed partial class TrayFlyout : UserControl, INotifyPropertyChanged
{
    private readonly TelemetryStore _telemetry;

    public TrayFlyout(TelemetryStore telemetry)
    {
        _telemetry = telemetry;
        DataContext = telemetry;
        InitializeComponent();
        telemetry.Updated += (_, _) =>
        {
            if (IsVisible)
            {
                Refresh();
            }
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Refresh();
            }
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? OpenRequested;

    public event EventHandler? RestartRequested;

    public string LastMatchText => _telemetry.Snapshot is { } s ? TelemetryFormat.LastMatch(s.LastMatchMs) : "—";

    public string Avg5Text => TelemetryFormat.Dbm(_telemetry.Snapshot?.Avg?.W5);

    public string ZoneTone => _telemetry.Zone switch
    {
        SignalZone.Near => "ok",
        SignalZone.Away => "danger",
        _ => "neutral",
    };

    public string StateSubText => _telemetry.IsLeaving
        ? UiStrings.Overview_ConfirmingDeparture
        : _telemetry.Connected ? string.Format(UiStrings.Overview_StateSince, _telemetry.StateSinceText) : "";

    public bool CanRestart => _telemetry.ServiceStatus == ServiceStatusKind.Running;

    private void Refresh()
    {
        Chart.Points = _telemetry.History.Downsample(240);
        Chart.NowMs = _telemetry.Snapshot?.TsMs ?? 0;
        Chart.LockDbm = _telemetry.Snapshot?.Thr?.LockDbm ?? double.NaN;
        Chart.UnlockDbm = _telemetry.Snapshot?.Thr?.UnlockDbm ?? double.NaN;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private void OnOpenClicked(object sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, EventArgs.Empty);

    private void OnRestartClicked(object sender, RoutedEventArgs e) => RestartRequested?.Invoke(this, EventArgs.Empty);
}

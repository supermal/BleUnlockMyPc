using System.ComponentModel;
using System.Globalization;
using System.Windows.Threading;
using AutoUnlockManager.Core;
using AutoUnlockManager.Core.Management;
using AutoUnlockManager.Core.Telemetry;
using Wpf.Ui.Controls;

namespace AutoUnlockManager.Services;

/// <summary>
/// Telemetría de toda la aplicación. El servicio admite un único cliente en su canal de gestión,
/// así que este es el único <see cref="ManagementClient"/>: lo comparten la vista general, la barra
/// lateral, la barra de título y la bandeja. Los eventos del cliente llegan en hilos de fondo y se
/// aplican en el hilo de la UI; cada cambio notifica todas las propiedades a la vez.
/// </summary>
public sealed class TelemetryStore : ObservableObject, IDisposable
{
    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromSeconds(3);

    private readonly TelemetryModel _model = new();
    private readonly ManagementClient _client;
    private readonly ServiceControlService _serviceControl;
    private readonly TargetDeviceService _device;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _statusTimer;
    private bool _disposed;

    public TelemetryStore(ServiceControlService serviceControl, TargetDeviceService device)
    {
        _serviceControl = serviceControl;
        _device = device;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _device.PropertyChanged += OnDeviceChanged;

        _client = new ManagementClient();
        _client.FrameReceived += OnFrameReceived;
        _client.ConnectionChanged += OnConnectionChanged;

        _statusTimer = new DispatcherTimer(StatusPollInterval, DispatcherPriority.Background, (_, _) => _ = RefreshServiceStatusAsync(), _dispatcher);
        _statusTimer.Start();
        _ = RefreshServiceStatusAsync();
    }

    /// <summary>El histórico se vació (sesión nueva o generación nueva): la curva se redibuja entera.</summary>
    public event EventHandler? HistoryReset;

    /// <summary>Llegó una trama o cambió la conexión o el estado del servicio.</summary>
    public event EventHandler? Updated;

    public SampleHistory History => _model.History;

    public SnapshotFrame? Snapshot => _model.Snapshot;

    public bool Connected => _model.Connected;

    public double? CurrentDbm => _model.CurrentDbm;

    public string CurrentDbmText => CurrentDbm is { } dbm
        ? Math.Round(dbm, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)
        : "—";

    public SignalZone? Zone => _model.Zone;

    public string ZoneText => Zone is { } zone ? SignalZones.DisplayName(zone) : "";

    public SnapshotBatch? Batch => _model.Batch;

    public string? StateName => _model.StateName;

    public string StateText => TelemetryFormat.StateName(_model.StateName);

    public string StateSinceText => Snapshot is { } s ? TelemetryFormat.Since(s.State?.SinceMs, s.TsMs) : "—";

    public ServiceStatusKind ServiceStatus { get; private set; }

    public TrayState TrayState => _model.TrayState;

    public TrayTooltip Tooltip => _model.Tooltip(_device.DeviceName);

    public string DeviceName => _device.DeviceName;

    public string DeviceAddress => _device.DeviceAddress;

    // —— Distintivo de la barra de título ——
    public string ServiceBadgeTone => ServiceStatus switch
    {
        ServiceStatusKind.Running when Connected => "ok",
        ServiceStatusKind.Stopped or ServiceStatusKind.NotInstalled => "danger",
        _ => "neutral",
    };

    public string ServiceBadgeText => ServiceStatus switch
    {
        ServiceStatusKind.Running when Connected => "服务运行中",
        ServiceStatusKind.Stopped => "服务已停止",
        ServiceStatusKind.NotInstalled => "服务未安装",
        _ => "正在连接服务",
    };

    public SymbolRegular ServiceBadgeIcon => ServiceBadgeTone switch
    {
        "ok" => SymbolRegular.Checkmark12,
        "danger" => SymbolRegular.Dismiss12,
        _ => SymbolRegular.Circle12,
    };

    public async Task RefreshServiceStatusAsync()
    {
        ServiceStatusInfo info;
        try
        {
            info = await _serviceControl.GetStatusAsync();
        }
        catch
        {
            info = new ServiceStatusInfo(ServiceStatusKind.Unknown);
        }

        if (_disposed || info.Kind == ServiceStatus && _model.ServiceRunning is not null)
        {
            return;
        }

        ServiceStatus = info.Kind;
        _model.ServiceRunning = info.Kind switch
        {
            ServiceStatusKind.Running => true,
            ServiceStatusKind.Stopped or ServiceStatusKind.NotInstalled => false,
            _ => null,
        };
        Publish();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _statusTimer.Stop();
        _device.PropertyChanged -= OnDeviceChanged;
        _client.FrameReceived -= OnFrameReceived;
        _client.ConnectionChanged -= OnConnectionChanged;
        _client.Dispose();
    }

    private void OnFrameReceived(object? sender, ManagementFrame frame) =>
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            if (_model.Apply(frame))
            {
                HistoryReset?.Invoke(this, EventArgs.Empty);
            }

            Publish();
        });

    private void OnConnectionChanged(object? sender, bool connected) =>
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            _model.SetConnected(connected);
            Publish();
        });

    private void OnDeviceChanged(object? sender, PropertyChangedEventArgs e) => Publish();

    private void Publish()
    {
        // Cadena vacía = todas las propiedades cambiaron
        OnPropertyChanged(string.Empty);
        Updated?.Invoke(this, EventArgs.Empty);
    }
}

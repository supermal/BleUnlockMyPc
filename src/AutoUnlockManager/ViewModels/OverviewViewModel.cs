using System.Text;
using AutoUnlockManager.Core;
using AutoUnlockManager.Core.Management;
using AutoUnlockManager.Core.Telemetry;
using AutoUnlockManager.Services;
using Wpf.Ui.Controls;

namespace AutoUnlockManager.ViewModels;

/// <summary>Operación de control del servicio (botones de la vista general, previa confirmación).</summary>
public enum ServiceOperationKind
{
    Start,
    Stop,
    Restart,
}

/// <summary>Fila de la lista «服务与健康». Tone: ok | warn | danger | neutral | plain (texto sin distintivo).</summary>
public sealed record HealthRow(string Key, string Value, string Sub, string Tone);

/// <summary>
/// Vista general: señal en directo, ciclo de bloqueo, salud del servicio y dispositivo objetivo.
/// Los datos vienen de <see cref="TelemetryStore"/> (no abre un cliente propio); este modelo solo
/// los adapta a la página y ejecuta las operaciones del servicio.
/// </summary>
public sealed partial class OverviewViewModel : ObservableObject
{
    private readonly ServiceControlService _serviceControl;

    public OverviewViewModel(TelemetryStore telemetry, ServiceControlService serviceControl)
    {
        Telemetry = telemetry;
        _serviceControl = serviceControl;
        HealthRows = [];
        ChartPoints = [];
        BannerTitle = "";
        BannerMessage = "";
        Refresh();
        telemetry.Updated += (_, _) => Refresh();
    }

    public TelemetryStore Telemetry { get; }

    // —— Curva ——
    [ObservableProperty] public partial IReadOnlyList<SamplePoint> ChartPoints { get; set; }
    [ObservableProperty] public partial long ChartNowMs { get; set; }
    [ObservableProperty] public partial double LockDbm { get; set; } = double.NaN;
    [ObservableProperty] public partial double UnlockDbm { get; set; } = double.NaN;

    // —— Tarjeta de señal ——
    public string Avg5Text => TelemetryFormat.Dbm(Telemetry.Snapshot?.Avg?.W5);

    public string Avg10Text => TelemetryFormat.Dbm(Telemetry.Snapshot?.Avg?.W10);

    public string ZoneTone => Telemetry.Zone switch
    {
        SignalZone.Near => "ok",
        SignalZone.Away => "danger",
        _ => "neutral",
    };

    private bool IsLeaving => Telemetry.IsLeaving;

    public string StateTone => IsLeaving ? "warn" : "info";

    public string StateSubText => IsLeaving
        ? "正在确认是否离开"
        : Telemetry.Connected ? $"已持续 {Telemetry.StateSinceText}" : "";

    [ObservableProperty] public partial IReadOnlyList<HealthRow> HealthRows { get; set; }

    // —— Control del servicio ——
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(CanStopOrRestart))]
    public partial bool OperationInProgress { get; set; }

    public bool CanStart =>
        !OperationInProgress
        && Telemetry.ServiceStatus is ServiceStatusKind.Stopped;

    public bool CanStopOrRestart => !OperationInProgress && Telemetry.ServiceStatus is ServiceStatusKind.Running;

    // —— Avisos ——
    public bool ConnectionBannerVisible => !Telemetry.Connected;

    [ObservableProperty] public partial bool BannerOpen { get; set; }
    [ObservableProperty] public partial InfoBarSeverity BannerSeverity { get; set; }
    [ObservableProperty] public partial string BannerTitle { get; set; }
    [ObservableProperty] public partial string BannerMessage { get; set; }

    public void ShowBanner(InfoBarSeverity severity, string title, string message)
    {
        BannerSeverity = severity;
        BannerTitle = title;
        BannerMessage = message;
        BannerOpen = true;
    }

    /// <summary>Exporta las muestras del búfer (5 minutos) a CSV en UTF-8 sin BOM.</summary>
    public async Task ExportCsvAsync(string path)
    {
        try
        {
            var samples = Telemetry.History.Snapshot();
            await File.WriteAllTextAsync(path, SampleCsv.Build(samples), new UTF8Encoding(false));
            ShowBanner(InfoBarSeverity.Success, "已导出样本", $"已导出 {samples.Count} 条样本到：{path}");
        }
        catch (Exception ex)
        {
            ShowBanner(InfoBarSeverity.Error, "导出失败", ex.Message);
        }
    }

    public async Task RunServiceOperationAsync(ServiceOperationKind kind)
    {
        if (OperationInProgress)
        {
            return;
        }

        OperationInProgress = true;
        ServiceOperationResult result;
        try
        {
            result = kind switch
            {
                ServiceOperationKind.Start => await _serviceControl.StartAsync(),
                ServiceOperationKind.Stop => await _serviceControl.StopAsync(),
                _ => await _serviceControl.RestartAsync(),
            };
        }
        catch (Exception ex)
        {
            result = new ServiceOperationResult(ServiceOperationStatus.Failure, ex.Message);
        }

        OperationInProgress = false;
        var action = kind switch
        {
            ServiceOperationKind.Start => "启动",
            ServiceOperationKind.Stop => "停止",
            _ => "重启",
        };

        if (result.Status == ServiceOperationStatus.Success)
        {
            ShowBanner(InfoBarSeverity.Success, $"已{action}服务", $"AutoUnlockService 已{action}。");
        }
        else
        {
            ShowBanner(InfoBarSeverity.Error, $"{action}服务失败", DescribeFailure(result));
        }

        await Telemetry.RefreshServiceStatusAsync();
    }

    private void Refresh()
    {
        var snapshot = Telemetry.Snapshot;
        ChartPoints = Telemetry.History.Downsample();
        ChartNowMs = snapshot?.TsMs ?? ChartNowMs;
        LockDbm = snapshot?.Thr?.LockDbm ?? LockDbm;
        UnlockDbm = snapshot?.Thr?.UnlockDbm ?? UnlockDbm;

        var rows = BuildHealthRows(snapshot);
        if (!rows.SequenceEqual(HealthRows))
        {
            HealthRows = rows;
        }

        OnPropertyChanged(nameof(Avg5Text));
        OnPropertyChanged(nameof(Avg10Text));
        OnPropertyChanged(nameof(ZoneTone));
        OnPropertyChanged(nameof(StateTone));
        OnPropertyChanged(nameof(StateSubText));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStopOrRestart));
        OnPropertyChanged(nameof(ConnectionBannerVisible));
    }

    private IReadOnlyList<HealthRow> BuildHealthRows(SnapshotFrame? snapshot)
    {
        var service = Telemetry.ServiceStatus switch
        {
            ServiceStatusKind.Running => new HealthRow("服务", "正在运行",
                snapshot is null ? "" : $"已运行 {TelemetryFormat.Uptime(snapshot.UptimeSec)}", "ok"),
            ServiceStatusKind.Stopped => new HealthRow("服务", "已停止", "自动锁定与解锁已暂停", "danger"),
            ServiceStatusKind.NotInstalled => new HealthRow("服务", "未安装", "请运行安装脚本", "danger"),
            ServiceStatusKind.StartPending => new HealthRow("服务", "正在启动…", "", "neutral"),
            ServiceStatusKind.StopPending => new HealthRow("服务", "正在停止…", "", "neutral"),
            _ => new HealthRow("服务", "查询中…", "", "neutral"),
        };

        if (snapshot is null)
        {
            return
            [
                service,
                new HealthRow("BLE 扫描", "—", "", "plain"),
                new HealthRow("提示助手", "—", "", "plain"),
                new HealthRow("凭据提供程序", "—", "", "plain"),
                new HealthRow("最近匹配", "—", "", "plain"),
            ];
        }

        var scanStatus = snapshot.Scan?.Status;
        var scan = TelemetryFormat.ClassifyScan(scanStatus) switch
        {
            ScanHealth.Running => new HealthRow("BLE 扫描", "正常", "", "ok"),
            ScanHealth.Degraded => new HealthRow("BLE 扫描", "异常", scanStatus ?? "", "warn"),
            _ => new HealthRow("BLE 扫描", "—", "", "plain"),
        };

        var helper = snapshot.Helper is { Connected: true }
            ? new HealthRow("提示助手", "已连接", "", "ok")
            : new HealthRow("提示助手", "未连接", "无法弹出锁屏提示", "warn");

        // El proveedor de credenciales solo vive en la pantalla de bloqueo: desconectado es lo normal
        var cred = snapshot.Cred is { Connected: true }
            ? new HealthRow("凭据提供程序", "已连接", "", "ok")
            : new HealthRow("凭据提供程序", "未连接", "锁屏时才会连接", "neutral");

        var stale = TelemetryFormat.IsLastMatchStale(snapshot.LastMatchMs, snapshot.UptimeSec);
        var lastMatch = new HealthRow("最近匹配", TelemetryFormat.LastMatch(snapshot.LastMatchMs),
            stale ? "超过 5 分钟未收到目标设备信号" : "", stale ? "warn" : "plain");

        return [service, scan, helper, cred, lastMatch];
    }

    private static string DescribeFailure(ServiceOperationResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            return result.Message!;
        }

        return result.Status switch
        {
            ServiceOperationStatus.NotInstalled => "服务未安装（Win32 1060）。",
            ServiceOperationStatus.AccessDenied => "拒绝访问：需要管理员权限。",
            ServiceOperationStatus.Timeout => "等待服务状态变化超时。",
            _ => "未知错误。",
        };
    }
}

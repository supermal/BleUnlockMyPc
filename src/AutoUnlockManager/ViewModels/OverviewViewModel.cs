using System.Globalization;
using AutoUnlockManager.Core;
using AutoUnlockManager.Core.Management;
using AutoUnlockManager.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AutoUnlockManager.ViewModels;

/// <summary>服务控制操作类别（概览页按钮 → 确认后执行）。</summary>
public enum ServiceOperationKind
{
    Start,
    Stop,
    Restart,
}

/// <summary>曲线更新载荷（UI 线程内交付：已降采样点集 + 当前生效阈值 + 服务端当前时间）。</summary>
public sealed record ChartUpdate(IReadOnlyList<SamplePoint> Points, SnapshotThresholds? Thresholds, long NowMs);

/// <summary>
/// 概览页视图模型（设计 §5.3/§5.5）：管理通道客户端 + 5 分钟滚动样本缓冲 + SCM 服务控制 + CSV。
/// 客户端事件在后台线程到达，统一经 DispatcherQueue 调度到 UI 线程后再更新状态；
/// <see cref="Dispose"/> 在页面 Unloaded 时调用（释放管道客户端，避免占用服务端单客户端槽位）。
/// 属性采用 partial 属性形式（CommunityToolkit.Mvvm 对 WinUI 3 推荐写法，AOT 兼容）。
/// </summary>
public sealed partial class OverviewViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromSeconds(3);

    private readonly DispatcherQueue _dispatcher;
    private readonly ManagementClient _client;
    private readonly SampleHistory _history = new();
    private readonly ServiceControlService _serviceControl = new();
    private readonly DispatcherTimer _statusTimer = new();

    private long _lastGen = -1;
    private bool _disposed;

    // —— 连接 / 遥测（仅 UI 线程写） ——
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionBannerVisible))]
    public partial bool Connected { get; set; }

    [ObservableProperty] public partial string CurrentDbmText { get; set; }
    [ObservableProperty] public partial string Avg5Text { get; set; }
    [ObservableProperty] public partial string Avg10Text { get; set; }
    [ObservableProperty] public partial string StateText { get; set; }
    [ObservableProperty] public partial string StateDurationText { get; set; }
    [ObservableProperty] public partial string ScanText { get; set; }
    [ObservableProperty] public partial ScanHealth ScanHealth { get; set; }
    [ObservableProperty] public partial string HelperText { get; set; }
    [ObservableProperty] public partial bool HelperConnected { get; set; }
    [ObservableProperty] public partial string CredText { get; set; }
    [ObservableProperty] public partial bool CredConnected { get; set; }
    [ObservableProperty] public partial string UptimeText { get; set; }
    [ObservableProperty] public partial string LastMatchText { get; set; }
    [ObservableProperty] public partial bool LastMatchStale { get; set; }

    // —— 服务控制 / 操作横幅 ——
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServiceStatusText))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanStopOrRestart))]
    public partial ServiceStatusKind ServiceStatus { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanStopOrRestart))]
    public partial bool OperationInProgress { get; set; }

    [ObservableProperty] public partial bool OperationBannerVisible { get; set; }
    [ObservableProperty] public partial InfoBarSeverity OperationBannerSeverity { get; set; }
    [ObservableProperty] public partial string OperationBannerTitle { get; set; }
    [ObservableProperty] public partial string OperationBannerMessage { get; set; }

    public OverviewViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;

        // partial 属性的初始值在构造函数设置（生成器不支持声明处初始化）
        CurrentDbmText = "—";
        Avg5Text = "—";
        Avg10Text = "—";
        StateText = "未知";
        StateDurationText = "—";
        ScanText = "—";
        HelperText = "—";
        CredText = "—";
        UptimeText = "—";
        LastMatchText = "—";
        OperationBannerTitle = "";
        OperationBannerMessage = "";

        _client = new ManagementClient();
        _client.FrameReceived += OnFrameReceived;
        _client.ConnectionChanged += OnConnectionChanged;

        _statusTimer.Interval = StatusPollInterval;
        _statusTimer.Tick += OnStatusTimerTick;
        _statusTimer.Start();

        _ = RefreshServiceStatusAsync();
    }

    /// <summary>曲线数据更新（UI 线程内触发）。</summary>
    public event EventHandler<ChartUpdate>? ChartUpdated;

    /// <summary>断线横幅：未连接即显示（客户端自动重试中，设计 §5.5）。</summary>
    public bool ConnectionBannerVisible => !Connected;

    /// <summary>启动可用：非运行且非过渡态、无操作进行中（与托盘同规则）。</summary>
    public bool CanStart =>
        !OperationInProgress
        && ServiceStatus is not (ServiceStatusKind.Running or ServiceStatusKind.StartPending or ServiceStatusKind.StopPending);

    /// <summary>停止/重启可用：仅运行态、无操作进行中（与托盘同规则）。</summary>
    public bool CanStopOrRestart => !OperationInProgress && ServiceStatus is ServiceStatusKind.Running;

    /// <summary>服务状态中文（健康行展示）。</summary>
    public string ServiceStatusText => ServiceStatus switch
    {
        ServiceStatusKind.Running => "正在运行",
        ServiceStatusKind.Stopped => "已停止",
        ServiceStatusKind.StartPending => "正在启动…",
        ServiceStatusKind.StopPending => "正在停止…",
        ServiceStatusKind.NotInstalled => "未安装",
        _ => "查询中…",
    };

    /// <summary>当前缓冲样本数（导出结果提示用）。</summary>
    public int SampleCount => _history.Count;

    /// <summary>构建样本 CSV（时间升序；含表头）。</summary>
    public string BuildCsv() => SampleCsv.Build(_history.Snapshot());

    /// <summary>显示操作横幅（导出等页面动作复用）。</summary>
    public void ShowOperationMessage(string title, string message, InfoBarSeverity severity)
    {
        OperationBannerSeverity = severity;
        OperationBannerTitle = title;
        OperationBannerMessage = message;
        OperationBannerVisible = true;
    }

    /// <summary>执行服务控制（页面确认后调用）：结果横幅 + 状态刷新。</summary>
    public async Task RunServiceOperationAsync(ServiceOperationKind kind)
    {
        if (_disposed || OperationInProgress)
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
                ServiceOperationKind.Restart => await _serviceControl.RestartAsync(),
                _ => new ServiceOperationResult(ServiceOperationStatus.Failure, "未知操作。"),
            };
        }
        catch (Exception ex)
        {
            result = new ServiceOperationResult(ServiceOperationStatus.Failure, ex.Message);
        }

        if (_disposed)
        {
            return;
        }

        OperationInProgress = false;

        var action = kind switch
        {
            ServiceOperationKind.Start => "启动",
            ServiceOperationKind.Stop => "停止",
            ServiceOperationKind.Restart => "重启",
            _ => "操作",
        };

        if (result.Status == ServiceOperationStatus.Success)
        {
            ShowOperationMessage($"已{action}服务", $"AutoUnlockService 已{action}。", InfoBarSeverity.Success);
        }
        else
        {
            ShowOperationMessage($"{action}服务失败", DescribeFailure(result), InfoBarSeverity.Error);
        }

        await RefreshServiceStatusAsync();
    }

    /// <summary>3 秒轮询查询服务状态（与托盘节奏一致）。</summary>
    public async Task RefreshServiceStatusAsync()
    {
        if (_disposed)
        {
            return;
        }

        ServiceStatusInfo info;
        try
        {
            info = await _serviceControl.GetStatusAsync();
        }
        catch
        {
            info = new ServiceStatusInfo(ServiceStatusKind.Unknown);
        }

        if (!_disposed)
        {
            ServiceStatus = info.Kind;
        }
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

    private void OnStatusTimerTick(object? sender, object e) => _ = RefreshServiceStatusAsync();

    private void OnFrameReceived(object? sender, ManagementFrame frame)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed)
            {
                return;
            }

            switch (frame)
            {
                case HelloFrame:
                    // 新会话：清空曲线与代次并复位会话相关展示，等待首帧 5 分钟回溯重新回填（设计 §5.5）
                    _history.Clear();
                    _lastGen = -1;
                    CurrentDbmText = "—";
                    Avg5Text = "—";
                    Avg10Text = "—";
                    StateText = "未知";
                    StateDurationText = "—";
                    ChartUpdated?.Invoke(this, new ChartUpdate([], null, 0));
                    break;
                case SnapshotFrame snapshot:
                    ApplySnapshot(snapshot);
                    break;
            }
        });
    }

    private void OnConnectionChanged(object? sender, bool connected)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (!_disposed)
            {
                Connected = connected;
            }
        });
    }

    private void ApplySnapshot(SnapshotFrame snapshot)
    {
        Connected = true; // 收到数据即视为连接有效

        if (snapshot.Gen != _lastGen)
        {
            _history.Clear(); // 历史重置（服务重启/缓冲清空）：曲线从首帧回溯重建
            _lastGen = snapshot.Gen;
        }

        _history.AddRange(snapshot.Samples);
        _history.Prune(snapshot.TsMs);

        CurrentDbmText = _history.Latest is { } latest
            ? latest.Rssi.ToString("0.0", CultureInfo.InvariantCulture)
            : "—";
        Avg5Text = TelemetryFormat.Dbm(snapshot.Avg?.W5);
        Avg10Text = TelemetryFormat.Dbm(snapshot.Avg?.W10);

        StateText = TelemetryFormat.StateName(snapshot.State?.Name);
        StateDurationText = TelemetryFormat.Since(snapshot.State?.SinceMs, snapshot.TsMs);

        var scanStatus = snapshot.Scan?.Status;
        ScanText = string.IsNullOrWhiteSpace(scanStatus) ? "—" : scanStatus!;
        ScanHealth = TelemetryFormat.ClassifyScan(scanStatus);

        var helper = snapshot.Helper;
        HelperConnected = helper?.Connected ?? false;
        HelperText = helper is null ? "—" : helper.Connected ? "已连接" : "未连接";

        var cred = snapshot.Cred;
        CredConnected = cred?.Connected ?? false;
        CredText = cred is null ? "—" : cred.Connected ? "已连接" : "未连接";

        UptimeText = TelemetryFormat.Uptime(snapshot.UptimeSec);
        LastMatchText = TelemetryFormat.LastMatch(snapshot.LastMatchMs);
        LastMatchStale = TelemetryFormat.IsLastMatchStale(snapshot.LastMatchMs, snapshot.UptimeSec);

        ChartUpdated?.Invoke(this, new ChartUpdate(_history.Downsample(), snapshot.Thr, snapshot.TsMs));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _statusTimer.Stop();
        _client.FrameReceived -= OnFrameReceived;
        _client.ConnectionChanged -= OnConnectionChanged;
        _client.Dispose();
    }
}

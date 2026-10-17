using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AutoUnlockManager.Services;

/// <summary>
/// 托盘宿主（设计 §5.4）：四色状态图标 + 动态菜单（显示主窗口｜服务状态｜启动/停止/重启｜退出）
/// + ~3s 状态轮询。托盘逻辑全部收敛在此类（设计 §9 回退路径）：
/// 任何异常只影响托盘刷新，不得让应用崩溃。
/// 菜单项经 Command 执行（默认 PopupMenu 模式库契约：只执行 Command、不触发 Click）。
/// </summary>
internal sealed class TrayIconHost : IDisposable
{
    /// <summary>状态轮询间隔。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    /// <summary>图标位图尺寸（托盘 100% DPI 为 16px，32px 覆盖高 DPI 缩放）。</summary>
    private const int IconSize = 32;

    /// <summary>见 <see cref="PinMessageHandlerDelegate"/>：进程级委托固定列表（只增不减）。</summary>
    private static readonly List<object> PinnedMessageHandlers = [];

    private readonly MainWindow _window;
    private readonly ServiceControlService _serviceControl = new();
    private readonly TaskbarIcon _taskbarIcon;
    private readonly MenuFlyoutItem _statusItem;
    private readonly MenuFlyoutItem _startItem;
    private readonly MenuFlyoutItem _stopItem;
    private readonly MenuFlyoutItem _restartItem;
    // 持有命令引用：状态变化后需 NotifyCanExecuteChanged（见 NotifyCommandStates 注释）
    private readonly RelayCommand _startCommand;
    private readonly RelayCommand _stopCommand;
    private readonly RelayCommand _restartCommand;
    private readonly DispatcherTimer _pollTimer;
    private readonly Dictionary<ServiceStatusKind, Icon?> _statusIcons = new();

    private bool _disposed;
    private bool _operationInProgress;
    private ServiceStatusKind _lastKind = ServiceStatusKind.Unknown; // 最近应用的状态（命令 CanExecute 与显示使能同源）

    public TrayIconHost(MainWindow window)
    {
        _window = window;

        // 四色状态图标：运行时自绘 + 缓存；自绘失败时回退 Assets\AppIcon.ico
        var fallbackIcon = TryLoadFallbackIcon();
        foreach (var kind in Enum.GetValues<ServiceStatusKind>())
        {
            _statusIcons[kind] = TryCreateStatusIcon(kind) ?? fallbackIcon;
        }

        _statusItem = new MenuFlyoutItem { Text = "服务状态：未知", IsEnabled = false }; // 只读状态行

        // 菜单项必须经 Command 执行：H.NotifyIcon 默认 ContextMenuMode=PopupMenu 把 MenuFlyoutItem
        // 转为 Win32 菜单时只执行 Command（TryExecute），从不触发 Click 事件（v2.4.1 库契约）
        _startCommand = new RelayCommand(ExecuteStartService, CanStartService);
        _stopCommand = new RelayCommand(ExecuteStopService, CanStopOrRestartService);
        _restartCommand = new RelayCommand(ExecuteRestartService, CanStopOrRestartService);
        _startItem = new MenuFlyoutItem
        {
            Text = "启动服务",
            Command = _startCommand,
        };
        _stopItem = new MenuFlyoutItem
        {
            Text = "停止服务",
            Command = _stopCommand,
        };
        _restartItem = new MenuFlyoutItem
        {
            Text = "重启服务",
            Command = _restartCommand,
        };

        var showItem = new MenuFlyoutItem
        {
            Text = "显示主窗口",
            Command = new RelayCommand(ExecuteShowWindow),
        };
        var exitItem = new MenuFlyoutItem
        {
            Text = "退出",
            Command = new RelayCommand(ExecuteExit),
        };

        var menu = new MenuFlyout();
        menu.Items.Add(showItem);
        menu.Items.Add(_statusItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(_startItem);
        menu.Items.Add(_stopItem);
        menu.Items.Add(_restartItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(exitItem);

        _taskbarIcon = new TaskbarIcon
        {
            ToolTipText = "AutoUnlock 管理器",
            MenuActivation = PopupActivationMode.RightClick,
            // H.NotifyIcon.WinUI 通过继承自 UIElement 的 ContextFlyout 接收托盘右键菜单
            ContextFlyout = menu,
            DoubleClickCommand = new RelayCommand(() => _window.ShowMainWindow()),
        };
        try
        {
            ApplyIcon(ServiceStatusKind.Unknown);
            _taskbarIcon.ForceCreate(); // 无 XAML 容器场景：必须显式创建
        }
        catch
        {
            // 崩溃防护（2026-10-17 复盘）：库的 Create() 先建隐藏消息窗口、再调 Shell_NotifyIcon；
            // 后者失败（如登录早期 shell 未就绪）即抛异常。此时若只丢弃引用：GC 在终结器线程上
            // 调 DestroyWindow 会因跨线程失败，留下“孤儿窗口”；其 WNDPROC 委托被回收后，.NET 10
            // 会在窗口收到任何消息时 FailFast 杀进程（0x80131623，已在本机 1:1 复现）。
            // 因此创建失败必须在本线程（UI 线程）显式 Dispose，让 DestroyWindow 成功、窗口不残留。
            try
            {
                _taskbarIcon.Dispose();
            }
            catch
            {
                // 释放失败不得掩盖原始异常
            }

            throw;
        }

        PinMessageHandlerDelegate(_taskbarIcon.TrayIcon.MessageWindow);

        _pollTimer = new DispatcherTimer { Interval = PollInterval };
        _pollTimer.Tick += OnPollTick;
        _pollTimer.Start();

        _ = RefreshStatusAsync(); // 启动后立即刷新一次（异步失败路径已在内部兜底）
    }

    /// <summary>
    /// 菜单命令：退出 —— 置退出流程 → 托盘先行 Dispose（防残影）→ 请求主窗口真实退出。
    /// </summary>
    private void ExecuteExit()
    {
        Dispose();
        try
        {
            _window.RequestExit();
        }
        catch
        {
            // 退出路径忽略异常
        }
    }

    /// <summary>菜单命令：显示主窗口。</summary>
    private void ExecuteShowWindow()
    {
        try
        {
            _window.ShowMainWindow();
        }
        catch
        {
            // 显示失败不影响托盘
        }
    }

    /// <summary>菜单命令：启动服务（后台执行；结果分类由 ServiceControlService 完成）。</summary>
    private void ExecuteStartService() => _ = RunServiceOperationAsync(ct => _serviceControl.StartAsync(ct));

    /// <summary>菜单命令：停止服务（后台执行）。</summary>
    private void ExecuteStopService() => _ = RunServiceOperationAsync(ct => _serviceControl.StopAsync(ct));

    /// <summary>菜单命令：重启服务（后台执行）。</summary>
    private void ExecuteRestartService() => _ = RunServiceOperationAsync(ct => _serviceControl.RestartAsync(ct));

    /// <summary>启动可用：非运行且非过渡态、无操作进行中（与菜单显示使能同源规则）。</summary>
    private bool CanStartService() =>
        !_operationInProgress
        && _lastKind is not (ServiceStatusKind.Running or ServiceStatusKind.StartPending or ServiceStatusKind.StopPending);

    /// <summary>停止/重启可用：仅运行态、无操作进行中（与菜单显示使能同源规则）。</summary>
    private bool CanStopOrRestartService() =>
        !_operationInProgress && _lastKind is ServiceStatusKind.Running;

    private void OnPollTick(object? sender, object e)
    {
        if (_disposed)
        {
            return;
        }
        _ = RefreshStatusAsync();
    }

    /// <summary>
    /// 执行一次服务控制操作：进行中禁操作项防重入；结束后刷新状态。
    /// 失败经托盘气泡反馈（Task 3 评审 F4）；页面内另有结果横幅。
    /// </summary>
    private async Task RunServiceOperationAsync(Func<CancellationToken, Task<ServiceOperationResult>> operation)
    {
        if (_disposed || _operationInProgress)
        {
            return;
        }

        _operationInProgress = true;
        DisableOperationItems();
        try
        {
            ServiceOperationResult? result = null;
            try
            {
                result = await operation(CancellationToken.None);
            }
            catch
            {
                // 结果分类已由 ServiceControlService 完成；此处兜底未预期异常
            }

            _operationInProgress = false;
            await RefreshStatusAsync();

            // Task 3 评审 F4：托盘发起的失败操作必须有可见反馈（成功不打扰）；
            // 操作函数异常时兜底为 Failure，避免静默
            result ??= new ServiceOperationResult(ServiceOperationStatus.Failure, "操作未完成：未预期的异常。");
            if (result.Status != ServiceOperationStatus.Success)
            {
                ShowFailureNotification(result);
            }
        }
        catch
        {
            // 任何未预期异常不得影响应用
        }
        finally
        {
            _operationInProgress = false;
        }
    }

    /// <summary>托盘操作失败气泡（Task 3 评审 F4）：分类文案 + 消息；气泡异常不得影响托盘。</summary>
    private void ShowFailureNotification(ServiceOperationResult result)
    {
        try
        {
            var message = result.Message;
            if (string.IsNullOrWhiteSpace(message))
            {
                message = result.Status switch
                {
                    ServiceOperationStatus.NotInstalled => "服务未安装（Win32 1060）。",
                    ServiceOperationStatus.AccessDenied => "拒绝访问：需要管理员权限。",
                    ServiceOperationStatus.Timeout => "等待服务状态变化超时。",
                    _ => "未知错误。",
                };
            }

            _taskbarIcon.ShowNotification("服务操作失败", message!, NotificationIcon.Warning);
        }
        catch
        {
            // 气泡失败不得影响托盘
        }
    }

    private async Task RefreshStatusAsync()
    {
        if (_disposed)
        {
            return;
        }

        ServiceStatusInfo status;
        try
        {
            status = await _serviceControl.GetStatusAsync();
        }
        catch (Exception ex)
        {
            // 轮询失败 → 未知（灰）
            status = new ServiceStatusInfo(ServiceStatusKind.Unknown, ex.Message);
        }

        if (_disposed)
        {
            return;
        }
        ApplyStatus(status);
    }

    private void ApplyStatus(ServiceStatusInfo status)
    {
        try
        {
            _statusItem.Text = "服务状态：" + DescribeStatus(status.Kind);
            try
            {
                ApplyIcon(status.Kind);
            }
            catch
            {
                // 图标刷新失败不阻塞使能更新（图标失败曾连带把整个菜单卡在灰态）
            }
            UpdateOperationItems(status.Kind);
        }
        catch
        {
            // 刷新失败仅影响托盘显示
        }
    }

    /// <summary>动态使能：启动=非运行时可点；停止/重启=运行时可点；过渡态全部禁用；操作进行中全部禁用。</summary>
    private void UpdateOperationItems(ServiceStatusKind kind)
    {
        _lastKind = kind; // 与命令 CanExecute 同源：显示使能与执行门控规则一致

        if (_operationInProgress)
        {
            DisableOperationItems();
            return;
        }

        var transitioning = kind is ServiceStatusKind.StartPending or ServiceStatusKind.StopPending;
        var running = kind is ServiceStatusKind.Running;
        _startItem.IsEnabled = !running && !transitioning;
        _stopItem.IsEnabled = running;
        _restartItem.IsEnabled = running;
        NotifyCommandStates();
    }

    private void DisableOperationItems()
    {
        _startItem.IsEnabled = false;
        _stopItem.IsEnabled = false;
        _restartItem.IsEnabled = false;
        NotifyCommandStates();
    }

    /// <summary>
    /// 通知三个服务命令重新求值 CanExecute。
    /// WinUI 的 MenuFlyoutItem 挂 Command 后，有效 IsEnabled = 本地值 &amp;&amp; 最近一次
    /// CanExecute 结果，且仅在 CanExecuteChanged 时重新求值——不通知则永远停留在挂接
    /// Command 时刻的状态（托盘「停止/重启」恒灰缺陷的根因）。
    /// </summary>
    private void NotifyCommandStates()
    {
        _startCommand.NotifyCanExecuteChanged();
        _stopCommand.NotifyCanExecuteChanged();
        _restartCommand.NotifyCanExecuteChanged();
    }

    private static string DescribeStatus(ServiceStatusKind kind) => kind switch
    {
        ServiceStatusKind.Running => "正在运行",
        ServiceStatusKind.Stopped => "已停止",
        ServiceStatusKind.NotInstalled => "未安装或不可用",
        ServiceStatusKind.StartPending => "正在启动…",
        ServiceStatusKind.StopPending => "正在停止…",
        _ => "未知",
    };

    private void ApplyIcon(ServiceStatusKind kind)
    {
        if (!_statusIcons.TryGetValue(kind, out var icon) || icon is null)
        {
            return;
        }

        // H.NotifyIcon 的 OnIconChanged 会 Dispose 被替换的旧 Icon（IL 实证：OnIconChanged → Icon.Dispose → UpdateIcon）。
        // 缓存原件不得直接复用：复用会在切回该状态时对新值读 Handle 抛 ObjectDisposedException（图标永久卡死）。
        // 每次交付克隆副本（库接管、替换时释放）；原件始终归本类所有，Dispose 时统一释放。
        var trayIcon = (Icon)icon.Clone();
        _taskbarIcon.Icon = trayIcon;
        _taskbarIcon.UpdateIcon(trayIcon);
    }

    /// <summary>状态 → 色（设计 §5.4）：绿=运行；红=停止或未安装；灰=查询失败/未知（含过渡态）。</summary>
    private static Color ColorOf(ServiceStatusKind kind) => kind switch
    {
        ServiceStatusKind.Running => TrayStatusColors.Running,
        ServiceStatusKind.Stopped or ServiceStatusKind.NotInstalled => TrayStatusColors.StoppedOrNotInstalled,
        _ => TrayStatusColors.Unknown,
    };

    /// <summary>
    /// 自绘 32×32 圆点状态图标（运行时生成 + 缓存）。GetHicon 返回的 HICON 不归 Icon 所有：
    /// Clone 出托管副本后立刻 DestroyIcon 销毁原句柄，避免 GDI 句柄泄漏。
    /// </summary>
    private static Icon? TryCreateStatusIcon(ServiceStatusKind kind)
    {
        try
        {
            using var bitmap = new Bitmap(IconSize, IconSize, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);
                using var fill = new SolidBrush(ColorOf(kind));
                graphics.FillEllipse(fill, 2, 2, IconSize - 4, IconSize - 4);
                using var outline = new Pen(Color.FromArgb(90, 0, 0, 0), 2f);
                graphics.DrawEllipse(outline, 2, 2, IconSize - 4, IconSize - 4);
            }

            var handle = bitmap.GetHicon();
            try
            {
                using var fromHandle = Icon.FromHandle(handle);
                return (Icon)fromHandle.Clone();
            }
            finally
            {
                DestroyIcon(handle);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>兜底托盘图标：Assets\AppIcon.ico（随发布产物分发）。</summary>
    /// <summary>
    /// 崩溃防护（纵深防御，2026-10-17 复盘）：把 H.NotifyIcon 隐藏消息窗口的 WNDPROC 委托
    /// （<c>MessageWindow.MessageHandler</c>，2.4.1 的私有属性）固定到进程级静态根。
    /// 库缺陷：该委托仅被 MessageWindow 实例引用；引用链一旦意外丢失，委托会随实例被 GC 回收，
    /// 而“孤儿窗口”仍存在于系统；此后任何窗口消息都会触发 .NET 10 的
    /// “callback on garbage collected delegate” FailFast（0x80131623）。
    /// 委托被固定后其 Target（MessageWindow 实例）保持存活，即使链路丢失也不会崩溃；
    /// 代价仅为进程生命周期内一个对象引用。库升级若属性改名/移除则静默跳过本防护。
    /// </summary>
    private static void PinMessageHandlerDelegate(MessageWindow messageWindow)
    {
        try
        {
            var property = typeof(MessageWindow).GetProperty(
                "MessageHandler",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var handler = property?.GetValue(messageWindow);
            if (handler is not null)
            {
                PinnedMessageHandlers.Add(handler);
            }
        }
        catch
        {
            // 固定失败不影响托盘功能
        }
    }

    private static Icon? TryLoadFallbackIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            return File.Exists(path) ? new Icon(path) : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        try
        {
            _pollTimer.Stop();
        }
        catch
        {
            // 忽略
        }
        try
        {
            _taskbarIcon.Dispose(); // 移出托盘，防退出后残影
        }
        catch
        {
            // 忽略
        }

        foreach (var icon in _statusIcons.Values.Distinct())
        {
            try
            {
                icon?.Dispose();
            }
            catch
            {
                // 忽略
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}

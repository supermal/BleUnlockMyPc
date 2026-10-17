using AutoUnlockManager.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AutoUnlockManager;

/// <summary>
/// 应用入口：单实例判定 → 创建主窗口 → 托盘常驻。
/// 关闭主窗口 = 隐藏到托盘；托盘不可用时 = 真实退出（见 <see cref="MainWindow.TrayAvailable"/>）。
/// 真实退出仅经托盘菜单「退出」（设计 §5.2）。
/// </summary>
public partial class App : Application
{
    private SingleInstanceGuard? _singleInstance;
    private MainWindow? _window;
    private TrayIconHost? _trayIcon;

    /// <summary>主窗口（FileSavePicker 等 WinRT 交互需要 HWND；null = 尚未创建或已退出）。</summary>
    public MainWindow? MainWindow => _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 单实例判定（最早时机）：二实例已置位唤醒事件（打开互斥体/事件被拒也按已有实例处理）
        // 后直接退出，不初始化窗口与托盘
        _singleInstance = SingleInstanceGuard.TryCreate(OnWakeupRequested);
        if (_singleInstance is null)
        {
            Environment.Exit(0);
            return;
        }

        _window = new MainWindow();
        _window.Closed += OnMainWindowClosed;
        _window.Activate();

        // 托盘初始化失败不影响主窗口可用（托盘层异常不得崩溃应用）；
        // 同时回填托盘可用性：托盘不可用时关闭窗口 = 真实退出（防无入口隐藏进程）
        TryInitializeTray();
    }

    /// <summary>托盘初始化已重试次数（初始尝试不计）。</summary>
    private int _trayRetryCount;

    /// <summary>托盘初始化重试上限（5 秒 × 6 ≈ 30 秒；登录早期 shell 就绪通常只需数秒）。</summary>
    private const int TrayMaxRetryCount = 6;

    /// <summary>托盘初始化重试间隔。</summary>
    private static readonly TimeSpan TrayRetryInterval = TimeSpan.FromSeconds(5);

    private DispatcherQueueTimer? _trayRetryTimer;

    /// <summary>
    /// 初始化托盘；失败时定时重试，直至成功或达到次数上限。
    /// 登录自启场景下 shell/任务栏可能尚未就绪（Shell_NotifyIcon 失败 → TrayIconHost 构造抛异常），
    /// 此时不放任托盘永久缺失，而是等待数秒后重试。
    /// </summary>
    private void TryInitializeTray()
    {
        var window = _window;
        if (window is null)
        {
            return;
        }

        try
        {
            _trayIcon = new TrayIconHost(window);
            window.TrayAvailable = true;
            StopTrayRetry();
        }
        catch
        {
            _trayIcon = null;
            window.TrayAvailable = false;
            ScheduleTrayRetry(window);
        }
    }

    /// <summary>安排一次延迟重试（无剩余次数时不安排）。</summary>
    private void ScheduleTrayRetry(MainWindow window)
    {
        if (_trayRetryCount >= TrayMaxRetryCount)
        {
            return;
        }
        _trayRetryCount++;

        if (_trayRetryTimer is null)
        {
            var timer = window.DispatcherQueue.CreateTimer();
            timer.Interval = TrayRetryInterval;
            timer.IsRepeating = false;
            timer.Tick += (_, _) => TryInitializeTray();
            _trayRetryTimer = timer;
        }

        _trayRetryTimer.Start();
    }

    /// <summary>停止未决的托盘重试（初始化成功或应用退出时调用）。</summary>
    private void StopTrayRetry()
    {
        try
        {
            _trayRetryTimer?.Stop();
        }
        catch
        {
            // 停止失败无碍：窗口关闭/应用退出后计时器自然失效
        }
    }

    /// <summary>单实例唤醒回调（后台线程触发）：切换到 UI 线程显示主窗口。</summary>
    private void OnWakeupRequested()
    {
        try
        {
            var window = _window;
            if (window is null)
            {
                return;
            }

            window.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    _window?.ShowMainWindow();
                }
                catch
                {
                    // 唤出失败不得影响应用
                }
            });
        }
        catch
        {
            // 同上：唤醒链路异常一律吞掉
        }
    }

    /// <summary>窗口关闭（= 真实退出流程）：先销毁托盘（防残影），释放单实例资源，最后兜底请求应用退出。</summary>
    private void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        StopTrayRetry(); // 退出路径：取消未决的托盘重试
        try
        {
            _trayIcon?.Dispose();
        }
        catch
        {
            // 退出路径：忽略清理异常，保证进程可退出
        }
        try
        {
            _singleInstance?.Dispose();
        }
        catch
        {
            // 同上
        }
        try
        {
            Application.Current?.Exit(); // 关闭最后窗口 = 退出应用（覆盖托盘不可用路径）
        }
        catch
        {
            // 同上
        }
    }
}

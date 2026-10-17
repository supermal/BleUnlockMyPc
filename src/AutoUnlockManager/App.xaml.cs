using AutoUnlockManager.Services;
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
        try
        {
            _trayIcon = new TrayIconHost(_window);
        }
        catch
        {
            _trayIcon = null;
        }
        _window.TrayAvailable = _trayIcon is not null;
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

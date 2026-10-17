using AutoUnlockManager.Services;
using AutoUnlockManager.Views;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace AutoUnlockManager;

/// <summary>
/// 主窗口：NavigationView 三页导航（概览/配置/日志）。
/// 关闭按钮 = 隐藏到托盘；托盘不可用时关闭 = 真实退出（见 <see cref="TrayAvailable"/>，设计 §5.2）。
/// 真实退出经托盘菜单「退出」→ <see cref="RequestExit"/>。
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>真实退出标志：仅 <see cref="RequestExit"/> 置位，放行关闭拦截。</summary>
    private bool _exitRequested;

    /// <summary>
    /// 托盘可用性（App 在托盘初始化完成后回填）。托盘不可用时关闭窗口 = 真实退出，
    /// 避免形成无托盘、无入口的隐藏进程；默认 true 保持「关闭=隐藏」语义。
    /// </summary>
    public bool TrayAvailable { get; set; } = true;

    public MainWindow()
    {
        InitializeComponent();

        Title = "AutoUnlock 管理器";
        // 默认尺寸约 1100×750（窗口位置记忆见 backlog，本任务不做）
        AppWindow.Resize(new SizeInt32(1100, 750));
        TrySetIcon();
        TryApplyModernChrome();
        ThemeService.Initialize(this);
        AppWindow.Closing += OnAppWindowClosing;

        // 默认选中「概览」（SelectionChanged 触发首页导航）
        if (NavView.MenuItems[0] is NavigationViewItem first)
        {
            NavView.SelectedItem = first;
        }
        if (ContentFrame.CurrentSourcePageType is null)
        {
            ContentFrame.Navigate(typeof(OverviewPage));
        }
    }

    /// <summary>
    /// 显示并唤出主窗口（托盘双击/菜单「显示主窗口」、单实例唤醒共用）：
    /// 已隐藏 → 显示；已最小化 → 还原；随后 Activate 置前。
    /// </summary>
    public void ShowMainWindow()
    {
        try
        {
            AppWindow.Show();
            (AppWindow.Presenter as OverlappedPresenter)?.Restore();
            Activate();
        }
        catch
        {
            // 窗口显示失败不影响调用方（托盘/唤醒链路）
        }
    }

    /// <summary>
    /// 真实退出：置退出标志后关闭窗口（放行关闭拦截），并兜底请求应用退出。
    /// 调用方（托盘「退出」）应先 <c>TrayIconHost.Dispose()</c> 防托盘残影。
    /// </summary>
    public void RequestExit()
    {
        _exitRequested = true;
        try
        {
            Close();
        }
        catch
        {
            // 继续走 Exit 兜底
        }
        try
        {
            Application.Current?.Exit();
        }
        catch
        {
            // 退出路径忽略异常
        }
    }

    /// <summary>关闭按钮拦截：托盘可用 → 取消并隐藏到托盘；托盘不可用 → 放行关闭（真实退出）。</summary>
    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exitRequested)
        {
            return; // 真实退出：放行
        }

        if (!TrayAvailable)
        {
            // 托盘不可用：无唤回入口，关闭即退出（防孤儿隐藏进程；Closed 回调兜底应用退出）
            _exitRequested = true;
            return; // 放行本次关闭
        }

        args.Cancel = true;
        try
        {
            AppWindow.Hide();
        }
        catch
        {
            // 隐藏失败（极罕见）：保持窗口存在即可
        }
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag })
        {
            return;
        }

        var pageType = tag switch
        {
            "config" => typeof(ConfigPage),
            "logs" => typeof(LogsPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(OverviewPage),
        };

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }

    /// <summary>
    /// WinUI 3 模板风格外观：Mica 背景（不支持时由 RootGrid 主题化背景兜底）+ 无系统标题栏的扩展标题栏（AppTitleBar 为拖拽区）。
    /// 标题栏按钮配色由 <see cref="ThemeService"/> 随主题同步；极端环境失败时静默退回默认外观。
    /// </summary>
    private void TryApplyModernChrome()
    {
        try
        {
            if (MicaController.IsSupported())
            {
                SystemBackdrop = new MicaBackdrop();
                RootGrid.Background = null; // 清空兜底背景，让 Mica 透出
            }

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
        }
        catch
        {
            // 新式外观失败仅影响观感
        }
    }

    /// <summary>设置窗口图标（Assets\AppIcon.ico，随发布产物分发）；失败不影响功能。</summary>
    private void TrySetIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }
        }
        catch
        {
            // 图标设置失败仅影响观感
        }
    }
}

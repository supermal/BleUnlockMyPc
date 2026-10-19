using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using AutoUnlockManager.Helpers;
using AutoUnlockManager.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace AutoUnlockManager.Views;

/// <summary>
/// 日志页（Task 6）：文件选择/实时跟随（暂停/继续追上）、四级别与关键字过滤、
/// WRN/ERR 行着色、导出当前过滤视图、打开所在文件夹。页面不缓存：离开即停止跟随循环。
/// 跟随滚动采用「底部才跟」模式：停在底部时新行推着列表走，向上翻看时列表保持位置、新行在下方叠加。
/// </summary>
public sealed partial class LogsPage : Page
{
    private ScrollViewer? _scrollViewer;
    private ItemsStackPanel? _itemsPanel;

    public LogsPage()
    {
        ViewModel = new LogsViewModel();
        InitializeComponent();
        ViewModel.ViewRebuilt += OnViewRebuilt;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        LogList.Loaded += OnLogListLoaded;
    }

    /// <summary>视图模型（x:Bind 数据源）。</summary>
    public LogsViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Initialize();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Stop();
        if (_scrollViewer is not null)
        {
            _scrollViewer.ViewChanged -= OnScrollViewChanged;
        }
    }

    // —— 跟随滚动（底部才跟） ——

    private void OnLogListLoaded(object sender, RoutedEventArgs e)
    {
        EnsureScrollInfrastructure();
        UpdateFollowMode();
    }

    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => UpdateFollowMode();

    /// <summary>
    /// 只有「已在底部且自动滚动开启」时才用 KeepLastItemInView 让新行推着列表走；
    /// 其余情况保持视口锚定（KeepItemsInView），新行在下方叠加、不打断阅读。
    /// </summary>
    private void UpdateFollowMode()
    {
        EnsureScrollInfrastructure();
        var atBottom = _scrollViewer is null ||
                       _scrollViewer.ScrollableHeight <= 0 ||
                       _scrollViewer.VerticalOffset >= _scrollViewer.ScrollableHeight - 8;

        if (_itemsPanel is not null)
        {
            _itemsPanel.ItemsUpdatingScrollMode = ViewModel.AutoScroll && atBottom
                ? ItemsUpdatingScrollMode.KeepLastItemInView
                : ItemsUpdatingScrollMode.KeepItemsInView;
        }
    }

    private void EnsureScrollInfrastructure()
    {
        if (_scrollViewer is null)
        {
            _scrollViewer = FindDescendant<ScrollViewer>(LogList);
            if (_scrollViewer is not null)
            {
                _scrollViewer.ViewChanged += OnScrollViewChanged;
            }
        }

        _itemsPanel ??= FindDescendant<ItemsStackPanel>(LogList);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LogsViewModel.AutoScroll))
        {
            return;
        }

        UpdateFollowMode();
        if (ViewModel.AutoScroll)
        {
            ScrollToEnd();
        }
    }

    private void OnViewRebuilt()
    {
        // 重建替换了 ItemsSource：等布局完成后回到底部（仅自动滚动开启时）
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateFollowMode();
            if (ViewModel.AutoScroll)
            {
                ScrollToEnd();
            }
        });
    }

    private void ScrollToEnd()
    {
        if (ViewModel.VisibleEntries.Count == 0)
        {
            return;
        }

        try
        {
            LogList.ScrollIntoView(ViewModel.VisibleEntries[^1]);
        }
        catch
        {
            // 布局尚未完成等瞬态：忽略本次滚动
        }
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    // —— 工具栏操作 ——

    private void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            ViewModel.RefreshFiles();
        }
        catch (Exception ex)
        {
            ViewModel.ShowBanner(InfoBarSeverity.Error, "刷新失败", ex.Message);
        }
    }

    private void OnTogglePauseClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            ViewModel.TogglePause();
        }
        catch (Exception ex)
        {
            ViewModel.ShowBanner(InfoBarSeverity.Error, "操作失败", ex.Message);
        }
    }

    private void OnOpenFolderClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = ViewModel.OpenFolderPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ViewModel.ShowBanner(InfoBarSeverity.Error, "打开文件夹失败", ex.Message);
        }
    }

    private async void OnExportClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ViewModel.VisibleCount == 0)
            {
                ViewModel.ShowBanner(InfoBarSeverity.Informational, "没有可导出的行", "当前过滤视图为空。");
                return;
            }

            var window = (Application.Current as App)?.MainWindow;
            if (window is null)
            {
                ViewModel.ShowBanner(InfoBarSeverity.Error, "导出失败", "主窗口尚未就绪，请稍后重试。");
                return;
            }

            var path = await SaveFilePicker.PickAsync(
                window.AppWindow.Id,
                $"autounlock-log-{DateTime.Now:yyyyMMdd-HHmmss}",
                "文本文件",
                [".txt"]);
            if (path is null)
            {
                return; // 用户取消
            }

            var text = ViewModel.BuildExportText();
            var count = ViewModel.VisibleCount; // 写盘前快照（写盘期间可能仍到达新行）
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
            ViewModel.ShowBanner(
                InfoBarSeverity.Success,
                "已导出日志",
                $"已导出 {count} 行到：{path}");
        }
        catch (Exception ex)
        {
            ViewModel.ShowBanner(InfoBarSeverity.Error, "导出失败", ex.Message);
        }
    }
}

using System.Diagnostics;
using System.Text;
using AutoUnlockManager.Helpers;
using AutoUnlockManager.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AutoUnlockManager.Views;

/// <summary>
/// 日志页（Task 6）：文件选择/实时跟随（暂停/继续追上）、四级别与关键字过滤、
/// WRN/ERR 行着色、导出当前过滤视图、打开所在文件夹。页面不缓存：离开即停止跟随循环。
/// </summary>
public sealed partial class LogsPage : Page
{
    public LogsPage()
    {
        ViewModel = new LogsViewModel();
        InitializeComponent();
        ViewModel.ViewRefreshed += OnViewRefreshed;
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
    }

    private void OnViewRefreshed()
    {
        if (!ViewModel.AutoScroll || ViewModel.VisibleEntries.Count == 0)
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

using System.ComponentModel;
using System.Text;
using AutoUnlockManager.Core;
using AutoUnlockManager.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using Windows.UI;

namespace AutoUnlockManager.Views;

/// <summary>
/// 概览页（Task 4）：实时 dBm/均值/状态芯片/曲线/健康行/服务控制（带确认）/CSV 导出。
/// ViewModel 持有管道客户端；页面 Unloaded 时释放（重新进入页面即重建并自动重连）。
/// </summary>
public sealed partial class OverviewPage : Page
{
    public OverviewPage()
    {
        ViewModel = new OverviewViewModel(DispatcherQueue);
        InitializeComponent();

        ViewModel.ChartUpdated += OnChartUpdated;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Unloaded += OnUnloaded;
        ActualThemeChanged += (_, _) => ApplyHealthColors();

        ApplyHealthColors();
    }

    /// <summary>视图模型（x:Bind 数据源）。</summary>
    public OverviewViewModel ViewModel { get; }

    private async void OnStartServiceClicked(object sender, RoutedEventArgs e)
        => await ConfirmAndRunAsync(ServiceOperationKind.Start);

    private async void OnStopServiceClicked(object sender, RoutedEventArgs e)
        => await ConfirmAndRunAsync(ServiceOperationKind.Stop);

    private async void OnRestartServiceClicked(object sender, RoutedEventArgs e)
        => await ConfirmAndRunAsync(ServiceOperationKind.Restart);

    private async Task ConfirmAndRunAsync(ServiceOperationKind kind)
    {
        var (title, content, primary) = kind switch
        {
            ServiceOperationKind.Start => ("启动服务", "要启动 AutoUnlockService 服务吗？", "启动"),
            ServiceOperationKind.Stop => ("停止服务", "停止后自动锁定/解锁将暂停，直至再次启动。确认停止？", "停止"),
            ServiceOperationKind.Restart => ("重启服务", "要重启 AutoUnlockService 服务吗？", "重启"),
            _ => ("服务操作", "要执行该操作吗？", "确定"),
        };

        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primary,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        ContentDialogResult result;
        try
        {
            result = await dialog.ShowAsync();
        }
        catch
        {
            return; // 极端场景（已有对话框展示/XamlRoot 失效）：忽略本次触发，不得使 async void 冒泡异常
        }

        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.RunServiceOperationAsync(kind);
        }
    }

    private async void OnExportCsvClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"autounlock-samples-{DateTime.Now:yyyyMMdd-HHmmss}",
            };
            picker.FileTypeChoices.Add("CSV 文件", new List<string> { ".csv" });

            var window = (Application.Current as App)?.MainWindow;
            if (window is not null)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
            }

            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return; // 用户取消
            }

            var csv = ViewModel.BuildCsv();
            var sampleCount = ViewModel.SampleCount; // 写盘前快照计数（写盘期间可能仍有新帧到达）
            await File.WriteAllTextAsync(file.Path, csv, new UTF8Encoding(false));
            ViewModel.ShowOperationMessage(
                "已导出样本",
                $"已导出 {sampleCount} 条样本到：{file.Path}",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ViewModel.ShowOperationMessage("导出失败", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void OnChartUpdated(object? sender, ChartUpdate update)
        => Chart.Update(update.Points, update.Thresholds, update.NowMs);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OverviewViewModel.ScanHealth)
            or nameof(OverviewViewModel.ScanText)
            or nameof(OverviewViewModel.HelperConnected)
            or nameof(OverviewViewModel.HelperText)
            or nameof(OverviewViewModel.CredConnected)
            or nameof(OverviewViewModel.CredText)
            or nameof(OverviewViewModel.LastMatchStale)
            or nameof(OverviewViewModel.LastMatchText))
        {
            ApplyHealthColors();
        }
    }

    /// <summary>
    /// 健康行着色（设计 §5.3：>5 分钟无匹配标黄；扫描异常/断连告警色）。
    /// 采用显式主题色（与 WinUI Fluent 标准色板一致），随 ActualThemeChanged 重算；
    /// 不依赖资源查找（页面资源字典对 ThemeResource 颜色键不生效的坑已规避）。
    /// </summary>
    private void ApplyHealthColors()
    {
        var dark = ActualTheme == ElementTheme.Dark;

        var text = Brush(dark ? 0xFFFFFFFFu : 0xE4000000u);
        var secondary = Brush(dark ? 0xB3FFFFFFu : 0x9E000000u);
        var caution = Brush(dark ? 0xFFFCE100u : 0xFF9D5D00u);
        var success = Brush(dark ? 0xFF6CCB5Fu : 0xFF0F7B0Fu);

        ScanValueText.Foreground = ViewModel.ScanHealth switch
        {
            ScanHealth.Running => text,
            ScanHealth.Degraded => caution,
            _ => secondary,
        };

        HelperValueText.Foreground = ViewModel.HelperText == "—"
            ? secondary
            : ViewModel.HelperConnected ? success : caution;
        CredValueText.Foreground = ViewModel.CredText == "—"
            ? secondary
            : ViewModel.CredConnected ? success : caution;

        LastMatchValueText.Foreground = ViewModel.LastMatchStale ? caution : text;
    }

    private static SolidColorBrush Brush(uint argb) => new(Color.FromArgb(
        (byte)(argb >> 24),
        (byte)(argb >> 16),
        (byte)(argb >> 8),
        (byte)argb));

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.ChartUpdated -= OnChartUpdated;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.Dispose();
    }
}

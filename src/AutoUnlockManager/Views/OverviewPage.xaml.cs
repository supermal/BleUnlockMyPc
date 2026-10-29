using System.Windows.Controls;
using AutoUnlockManager.Models;
using AutoUnlockManager.ViewModels;
using Microsoft.Win32;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;

namespace AutoUnlockManager.Views;

public partial class OverviewPage : UserControl
{
    private readonly IContentDialogService _dialogs;

    public OverviewPage(OverviewViewModel viewModel, IContentDialogService dialogs)
    {
        ViewModel = viewModel;
        _dialogs = dialogs;
        DataContext = viewModel;
        InitializeComponent();
    }

    public OverviewViewModel ViewModel { get; }

    private async void OnStartServiceClicked(object sender, RoutedEventArgs e) =>
        await ConfirmAndRunAsync(ServiceOperationKind.Start);

    private async void OnStopServiceClicked(object sender, RoutedEventArgs e) =>
        await ConfirmAndRunAsync(ServiceOperationKind.Stop);

    private async void OnRestartServiceClicked(object sender, RoutedEventArgs e) =>
        await ConfirmAndRunAsync(ServiceOperationKind.Restart);

    private async Task ConfirmAndRunAsync(ServiceOperationKind kind)
    {
        var (title, content, primary) = kind switch
        {
            ServiceOperationKind.Start => ("启动服务", "要启动 AutoUnlockService 服务吗？", "启动服务"),
            ServiceOperationKind.Stop => ("停止服务", "停止后自动锁定与解锁将暂停，直到再次启动。", "停止服务"),
            _ => ("重启服务", "要重启 AutoUnlockService 服务吗？", "重启服务"),
        };

        try
        {
            var result = await _dialogs.ShowSimpleDialogAsync(new SimpleContentDialogCreateOptions
            {
                Title = title,
                Content = content,
                PrimaryButtonText = primary,
                CloseButtonText = "取消",
            });

            if (result == ContentDialogResult.Primary)
            {
                await ViewModel.RunServiceOperationAsync(kind);
            }
        }
        catch (Exception ex)
        {
            // async void: ninguna excepción debe escapar al despachador
            ViewModel.ShowBanner(InfoBarSeverity.Error, "操作失败", ex.Message);
        }
    }

    private async void OnExportCsvClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            FileName = $"autounlock-samples-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            Filter = "CSV 文件 (*.csv)|*.csv",
            DefaultExt = ".csv",
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            await ViewModel.ExportCsvAsync(dialog.FileName);
        }
    }

    private void OnChangeDeviceClicked(object sender, RoutedEventArgs e) =>
        (Application.Current.MainWindow as MainWindow)?.Navigate(AppPage.Config);
}

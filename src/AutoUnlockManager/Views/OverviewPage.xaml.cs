using System.Windows.Controls;
using AutoUnlockManager.Models;
using AutoUnlockManager.ViewModels;
using Microsoft.Win32;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;
using AutoUnlockManager.Core.Resources;

namespace AutoUnlockManager.Views;

public partial class OverviewPage : UserControl
{
    private readonly IContentDialogService _dialogs;
    private Window? _window;

    public OverviewPage(OverviewViewModel viewModel, IContentDialogService dialogs)
    {
        ViewModel = viewModel;
        _dialogs = dialogs;
        DataContext = viewModel;
        InitializeComponent();

        // IsVisible cubre el cambio de página y la ventana oculta; minimizada sigue «visible»,
        // por eso se escucha también el estado de la ventana
        IsVisibleChanged += (_, _) => UpdateViewActive();
        Loaded += (_, _) => AttachWindow();
    }

    public OverviewViewModel ViewModel { get; }

    private void AttachWindow()
    {
        var window = Window.GetWindow(this);
        if (window != _window)
        {
            if (_window is not null)
            {
                _window.StateChanged -= OnWindowStateChanged;
            }

            _window = window;
            if (_window is not null)
            {
                _window.StateChanged += OnWindowStateChanged;
            }
        }

        UpdateViewActive();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e) => UpdateViewActive();

    private void UpdateViewActive() =>
        ViewModel.SetViewActive(IsVisible && _window?.WindowState != WindowState.Minimized);

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
            ServiceOperationKind.Start => (UiStrings.Common_StartService, UiStrings.Confirm_StartService, UiStrings.Common_StartService),
            ServiceOperationKind.Stop => (UiStrings.Common_StopService, UiStrings.Confirm_StopService, UiStrings.Common_StopService),
            _ => (UiStrings.Common_RestartService, UiStrings.Confirm_RestartService, UiStrings.Common_RestartService),
        };

        try
        {
            var result = await _dialogs.ShowSimpleDialogAsync(new SimpleContentDialogCreateOptions
            {
                Title = title,
                Content = content,
                PrimaryButtonText = primary,
                CloseButtonText = UiStrings.Common_Cancel,
            });

            if (result == ContentDialogResult.Primary)
            {
                await ViewModel.RunServiceOperationAsync(kind);
            }
        }
        catch (Exception ex)
        {
            // async void: ninguna excepción debe escapar al despachador
            ViewModel.ShowBanner(InfoBarSeverity.Error, UiStrings.Common_OperationFailed, ex.Message);
        }
    }

    private async void OnExportCsvClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            FileName = $"autounlock-samples-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            Filter = UiStrings.Overview_CsvFilter,
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

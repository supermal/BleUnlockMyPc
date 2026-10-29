using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Controls;
using AutoUnlockManager.Models;
using AutoUnlockManager.Services;
using AutoUnlockManager.Views;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace AutoUnlockManager;

public partial class MainWindow : FluentWindow
{
    private readonly IServiceProvider _services;
    private bool _exiting;
    private bool _syncingSelection;

    public MainWindow(IServiceProvider services, IContentDialogService dialogs, TelemetryStore telemetry)
    {
        _services = services;
        Telemetry = telemetry;

        MenuItems =
        [
            new NavItem(AppPage.Overview, "概览", SymbolRegular.Pulse24),
            new NavItem(AppPage.Config, "配置", SymbolRegular.Options24),
            new NavItem(AppPage.Logs, "日志", SymbolRegular.DocumentText24),
        ];
        FooterItems = [new NavItem(AppPage.Settings, "设置", SymbolRegular.Settings24)];

        InitializeComponent();
        dialogs.SetDialogHost(RootContentDialog);

        Navigate(AppPage.Overview);
    }

    /// <summary>Telemetría compartida (barra de título y vista rápida del dispositivo).</summary>
    public TelemetryStore Telemetry { get; }

    public ObservableCollection<NavItem> MenuItems { get; }

    public ObservableCollection<NavItem> FooterItems { get; }

    public AppPage CurrentPage { get; private set; }

    /// <summary>
    /// true cuando existe icono en la bandeja: cerrar la ventana solo la oculta.
    /// Sin bandeja, cerrar la ventana termina la aplicación.
    /// </summary>
    public bool TrayAvailable { get; set; }

    public void Navigate(AppPage page)
    {
        CurrentPage = page;
        PageHost.Content = page switch
        {
            AppPage.Overview => _services.GetRequiredService<OverviewPage>(),
            AppPage.Config => _services.GetRequiredService<ConfigPage>(),
            AppPage.Logs => _services.GetRequiredService<LogsPage>(),
            _ => _services.GetRequiredService<SettingsPage>(),
        };

        // Refleja la página en la navegación sin volver a disparar la selección
        _syncingSelection = true;
        MenuList.SelectedItem = MenuItems.FirstOrDefault(i => i.Page == page);
        FooterList.SelectedItem = FooterItems.FirstOrDefault(i => i.Page == page);
        _syncingSelection = false;
    }

    /// <summary>Muestra un diálogo propio sobre toda la ventana hasta que <paramref name="closed"/> se complete.</summary>
    public async Task ShowOverlayAsync(FrameworkElement dialog, Task closed)
    {
        OverlayContent.Content = dialog;
        OverlayLayer.Visibility = Visibility.Visible;
        try
        {
            await closed;
        }
        finally
        {
            OverlayLayer.Visibility = Visibility.Collapsed;
            OverlayContent.Content = null;
        }
    }

    /// <summary>Restaura y trae al frente la ventana (bandeja, segunda instancia).</summary>
    public void ShowMainWindow()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    /// <summary>Salida real (solo desde el menú de la bandeja o sin bandeja).</summary>
    public void ExitApplication()
    {
        _exiting = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting && TrayAvailable)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Application.Current.Shutdown();
    }

    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || sender is not ListBox { SelectedItem: NavItem item })
        {
            return;
        }

        Navigate(item.Page);
    }
}

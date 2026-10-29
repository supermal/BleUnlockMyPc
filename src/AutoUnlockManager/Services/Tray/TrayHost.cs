using System.Windows.Controls;
using System.Windows.Media;
using AutoUnlockManager.Core.Telemetry;
using AutoUnlockManager.Models;
using AutoUnlockManager.Views.Tray;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBlock = System.Windows.Controls.TextBlock;

namespace AutoUnlockManager.Services.Tray;

/// <summary>
/// Icono de la bandeja: clic = vista rápida, doble clic = ventana principal, botón derecho = menú.
/// El icono, el texto emergente y el estado del menú siguen a <see cref="TelemetryStore"/>.
/// Si la creación falla (shell aún no listo al iniciar sesión) el constructor lanza: el llamador
/// reintenta. Las operaciones del servicio desde aquí no piden confirmación; un fallo se avisa con
/// un globo (los éxitos no molestan).
/// </summary>
internal sealed class TrayHost : IDisposable
{
    private static readonly TimeSpan TooltipThrottle = TimeSpan.FromSeconds(1);

    private readonly MainWindow _window;
    private readonly TelemetryStore _telemetry;
    private readonly ServiceControlService _serviceControl;
    private readonly TaskbarIcon _icon;
    private readonly TrayFlyout _flyout;
    private readonly TextBlock _menuStatus = new() { FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _menuState = new() { Margin = new Thickness(12, 0, 0, 0) };
    private readonly TextBlock _menuDevice = new() { FontSize = 12 };
    private readonly SymbolIcon _menuBadge = new() { FontSize = 12, Margin = new Thickness(0, 0, 8, 0) };
    private readonly MenuItem _startItem;
    private readonly MenuItem _stopItem;
    private readonly MenuItem _restartItem;
    private (TrayState State, bool Light, int Size)? _shownIcon;
    private DateTime _lastTooltipUpdate;
    private bool _operationInProgress;
    private bool _disposed;

    public TrayHost(MainWindow window, TelemetryStore telemetry, ServiceControlService serviceControl)
    {
        _window = window;
        _telemetry = telemetry;
        _serviceControl = serviceControl;

        _flyout = new TrayFlyout(telemetry);
        _flyout.OpenRequested += (_, _) => ShowWindow();
        _flyout.RestartRequested += (_, _) => _ = RunServiceOperationAsync(ct => _serviceControl.RestartAsync(ct));

        _startItem = MenuEntry("启动服务", SymbolRegular.Play16, () => _ = RunServiceOperationAsync(ct => _serviceControl.StartAsync(ct)));
        _stopItem = MenuEntry("停止服务", SymbolRegular.Stop16, () => _ = RunServiceOperationAsync(ct => _serviceControl.StopAsync(ct)));
        _restartItem = MenuEntry("重启服务", SymbolRegular.ArrowClockwise16, () => _ = RunServiceOperationAsync(ct => _serviceControl.RestartAsync(ct)));

        _icon = new TaskbarIcon
        {
            ToolTipText = "AutoUnlock 管理器",
            TrayPopup = _flyout,
            PopupActivation = PopupActivationMode.LeftClick,
            ContextMenu = BuildMenu(),
            MenuActivation = PopupActivationMode.RightClick,
            DoubleClickCommand = new RelayCommand(ShowWindow),
        };

        try
        {
            UpdateIcon();
            _icon.ForceCreate(enablesEfficiencyMode: false);
        }
        catch
        {
            // Crear el icono falla si el shell aún no está listo: se libera aquí, en el hilo de la UI,
            // para no dejar una ventana oculta huérfana (lección de la versión WinUI, 2026-10-17)
            try
            {
                _icon.Dispose();
            }
            catch
            {
                // La excepción original es la que importa
            }

            throw;
        }

        _telemetry.Updated += OnTelemetryUpdated;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Refresh(force: true);
    }

    /// <summary>«退出管理器»: la aplicación termina; el servicio sigue funcionando.</summary>
    public event EventHandler? ExitRequested;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _telemetry.Updated -= OnTelemetryUpdated;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        try
        {
            _icon.Dispose();
        }
        catch
        {
            // Al salir no importa
        }
    }

    private ContextMenu BuildMenu()
    {
        // Cabecera informativa: no se puede seleccionar
        var statusLine = new StackPanel { Orientation = Orientation.Horizontal };
        statusLine.Children.Add(_menuBadge);
        statusLine.Children.Add(_menuStatus);
        statusLine.Children.Add(_menuState);
        _menuDevice.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Text.Secondary");
        _menuState.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Text.Secondary");
        var header = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        header.Children.Add(statusLine);
        header.Children.Add(new Border { Margin = new Thickness(20, 2, 0, 0), Child = _menuDevice });
        var status = new MenuItem { Header = header, IsHitTestVisible = false, Focusable = false };

        var open = MenuEntry("打开管理器", SymbolRegular.Window16, ShowWindow);
        open.FontWeight = FontWeights.SemiBold;
        var logs = MenuEntry("查看日志", SymbolRegular.DocumentText16, () =>
        {
            ShowWindow();
            _window.Navigate(AppPage.Logs);
        });

        var exitHeader = new StackPanel();
        exitHeader.Children.Add(new TextBlock { Text = "退出管理器" });
        var exitHint = new TextBlock { Text = "服务会继续在后台运行", FontSize = 12 };
        exitHint.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Text.Secondary");
        exitHeader.Children.Add(exitHint);
        var exit = new MenuItem
        {
            Header = exitHeader,
            Icon = new SymbolIcon(SymbolRegular.ArrowExit20) { FontSize = 16 },
        };
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new ContextMenu { MinWidth = 264 };
        menu.Items.Add(status);
        menu.Items.Add(new Separator());
        menu.Items.Add(open);
        menu.Items.Add(logs);
        menu.Items.Add(new Separator());
        menu.Items.Add(_startItem);
        menu.Items.Add(_stopItem);
        menu.Items.Add(_restartItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        menu.Opened += (_, _) => Refresh(force: true);
        return menu;
    }

    private static MenuItem MenuEntry(string text, SymbolRegular symbol, Action action)
    {
        var item = new MenuItem { Header = text, Icon = new SymbolIcon(symbol) { FontSize = 16 } };
        item.Click += (_, _) => action();
        return item;
    }

    private void ShowWindow()
    {
        try
        {
            _icon.CloseTrayPopup();
            _window.ShowMainWindow();
        }
        catch
        {
            // Mostrar la ventana no debe afectar a la bandeja
        }
    }

    private void OnTelemetryUpdated(object? sender, EventArgs e) => Refresh(force: false);

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        _window.Dispatcher.BeginInvoke(() => Refresh(force: true));

    private void Refresh(bool force)
    {
        if (_disposed)
        {
            return;
        }

        UpdateIcon();
        UpdateMenu();

        // El texto emergente lleva el dBm: cambia en cada trama, se limita a uno por segundo
        var now = DateTime.UtcNow;
        if (force || now - _lastTooltipUpdate >= TooltipThrottle)
        {
            _lastTooltipUpdate = now;
            _icon.ToolTipText = _telemetry.Tooltip.ToString();
        }
    }

    private void UpdateIcon()
    {
        var light = IsTaskbarLight();
        var size = IconSize();
        var key = (_telemetry.TrayState, light, size);
        if (_shownIcon == key)
        {
            return;
        }

        _icon.Icon = TrayIconRenderer.Render(key.TrayState, light, size);
        _shownIcon = key;
    }

    private void UpdateMenu()
    {
        var state = _telemetry.TrayState;
        _menuBadge.Symbol = state switch
        {
            TrayState.Running => SymbolRegular.CheckmarkCircle12,
            TrayState.Leaving or TrayState.Suspended => SymbolRegular.Warning12,
            TrayState.Stopped => SymbolRegular.DismissCircle12,
            _ => SymbolRegular.Circle12,
        };
        _menuBadge.SetResourceReference(Control.ForegroundProperty, state switch
        {
            TrayState.Running => "Ui.Status.Success.Fg",
            TrayState.Leaving => "Ui.Status.Warning.Fg",
            TrayState.Suspended or TrayState.Stopped => "Ui.Status.Danger.Fg",
            _ => "Ui.Text.Secondary",
        });
        _menuStatus.Text = _telemetry.ServiceBadgeText;
        _menuState.Text = _telemetry.Connected ? _telemetry.StateText : "";
        _menuDevice.Text = _telemetry.CurrentDbm is null
            ? _telemetry.DeviceName
            : $"{_telemetry.DeviceName} {_telemetry.CurrentDbmText} dBm";

        var status = _telemetry.ServiceStatus;
        _startItem.IsEnabled = !_operationInProgress && status == ServiceStatusKind.Stopped;
        _stopItem.IsEnabled = !_operationInProgress && status == ServiceStatusKind.Running;
        _restartItem.IsEnabled = !_operationInProgress && status == ServiceStatusKind.Running;
    }

    private async Task RunServiceOperationAsync(Func<CancellationToken, Task<ServiceOperationResult>> operation)
    {
        if (_disposed || _operationInProgress)
        {
            return;
        }

        _operationInProgress = true;
        UpdateMenu();
        ServiceOperationResult result;
        try
        {
            result = await operation(CancellationToken.None);
        }
        catch (Exception ex)
        {
            result = new ServiceOperationResult(ServiceOperationStatus.Failure, ex.Message);
        }
        finally
        {
            _operationInProgress = false;
        }

        await _telemetry.RefreshServiceStatusAsync();
        Refresh(force: true);

        if (result.Status != ServiceOperationStatus.Success)
        {
            ShowFailure(result);
        }
    }

    private void ShowFailure(ServiceOperationResult result)
    {
        var message = string.IsNullOrWhiteSpace(result.Message)
            ? result.Status switch
            {
                ServiceOperationStatus.NotInstalled => "服务未安装（Win32 1060）。",
                ServiceOperationStatus.AccessDenied => "拒绝访问：需要管理员权限。",
                ServiceOperationStatus.Timeout => "等待服务状态变化超时。",
                _ => "未知错误。",
            }
            : result.Message!;

        try
        {
            _icon.ShowNotification("服务操作失败", message, NotificationIcon.Warning);
        }
        catch
        {
            // El globo es informativo: si falla, la bandeja sigue
        }
    }

    // Tema de la barra de tareas (independiente del tema de las aplicaciones)
    private static bool IsTaskbarLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
        }
        catch
        {
            return false;
        }
    }

    private int IconSize()
    {
        var scale = _window.IsLoaded ? VisualTreeHelper.GetDpi(_window).DpiScaleX : 1.0;
        return (int)Math.Round(SystemParameters.SmallIconWidth * scale);
    }
}

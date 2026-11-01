using System.Windows.Controls;
using System.Windows.Media;
using BleUnlockManager.Core.Localization;
using BleUnlockManager.Core.Telemetry;
using BleUnlockManager.Localization;
using BleUnlockManager.Models;
using BleUnlockManager.Views.Tray;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBlock = System.Windows.Controls.TextBlock;
using BleUnlockManager.Core.Resources;

namespace BleUnlockManager.Services.Tray;

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
    private System.Drawing.Icon? _currentIcon;
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

        _startItem = MenuEntry("Common_StartService", SymbolRegular.Play16, () => _ = RunServiceOperationAsync(ct => _serviceControl.StartAsync(ct)));
        _stopItem = MenuEntry("Common_StopService", SymbolRegular.Stop16, () => _ = RunServiceOperationAsync(ct => _serviceControl.StopAsync(ct)));
        _restartItem = MenuEntry("Common_RestartService", SymbolRegular.ArrowClockwise16, () => _ = RunServiceOperationAsync(ct => _serviceControl.RestartAsync(ct)));

        _icon = new TaskbarIcon
        {
            ToolTipText = UiStrings.App_Title,
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
                _currentIcon?.Dispose();
            }
            catch
            {
                // La excepción original es la que importa
            }

            throw;
        }

        _telemetry.Updated += OnTelemetryUpdated;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        ManagerLanguage.Changed += OnLanguageChanged;
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
        ManagerLanguage.Changed -= OnLanguageChanged;
        try
        {
            _icon.Dispose();
            _currentIcon?.Dispose();
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

        var open = MenuEntry("Common_OpenManager", SymbolRegular.Window16, ShowWindow);
        open.FontWeight = FontWeights.SemiBold;
        var logs = MenuEntry("Tray_ViewLogs", SymbolRegular.DocumentText16, () =>
        {
            ShowWindow();
            _window.Navigate(AppPage.Logs);
        });

        var exitHeader = new StackPanel();
        var exitTitle = new TextBlock();
        exitTitle.SetBinding(TextBlock.TextProperty, Loc.Binding("Tray_Exit"));
        exitHeader.Children.Add(exitTitle);
        var exitHint = new TextBlock { FontSize = 12 };
        exitHint.SetBinding(TextBlock.TextProperty, Loc.Binding("Tray_ExitHint"));
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

    // El texto se enlaza a la clave de recursos: el menú cambia de idioma sin reconstruirse
    private static MenuItem MenuEntry(string textKey, SymbolRegular symbol, Action action)
    {
        var item = new MenuItem { Icon = new SymbolIcon(symbol) { FontSize = 16 } };
        item.SetBinding(HeaderedItemsControl.HeaderProperty, Loc.Binding(textKey));
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

    // El texto emergente y la cabecera del menú se rehacen ya, sin esperar a la siguiente trama
    private void OnLanguageChanged() => _window.Dispatcher.BeginInvoke(() => Refresh(force: true));

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        _window.Dispatcher.BeginInvoke(() => Refresh(force: true));

    private void Refresh(bool force)
    {
        if (_disposed)
        {
            return;
        }

        UpdateMenu();
        UpdateIcon();

        // El texto emergente lleva el dBm: cambia en cada trama, se limita a uno por segundo.
        // Shell_NotifyIcon falla de forma pasajera (p. ej. al volver de la suspensión, con el
        // Explorador aún ocupado) y H.NotifyIcon lo convierte en excepción: se ignora y se
        // reintenta con la siguiente trama
        var now = DateTime.UtcNow;
        if (force || now - _lastTooltipUpdate >= TooltipThrottle)
        {
            _lastTooltipUpdate = now;
            try
            {
                _icon.ToolTipText = _telemetry.Tooltip.ToString();
            }
            catch (InvalidOperationException)
            {
                // El valor ya quedó guardado: sin borrarlo, un texto idéntico no volvería a enviarse
                _lastTooltipUpdate = default;
                try { _icon.ClearValue(TaskbarIcon.ToolTipTextProperty); }
                catch (InvalidOperationException) { /* el siguiente intento lo enviará */ }
            }
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

        // Se usa UpdateIcon y no la propiedad Icon: si Shell_NotifyIcon falla, la propiedad lo ignora
        // y el icono se quedaría en el estado anterior. Aquí el fallo se ve y se reintenta en la
        // siguiente trama. El icono vigente se conserva: el shell lo reutiliza si el Explorador se reinicia
        var rendered = TrayIconRenderer.Render(key.TrayState, light, size);
        if (!_icon.UpdateIcon(rendered))
        {
            rendered.Dispose();
            _shownIcon = null;
            return;
        }

        _currentIcon?.Dispose();
        _currentIcon = rendered;
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
                ServiceOperationStatus.NotInstalled => UiStrings.Op_NotInstalled,
                ServiceOperationStatus.AccessDenied => UiStrings.Op_AccessDenied,
                ServiceOperationStatus.Timeout => UiStrings.Op_Timeout,
                _ => UiStrings.Op_UnknownError,
            }
            : result.Message!;

        try
        {
            _icon.ShowNotification(UiStrings.Tray_OperationFailed, message, NotificationIcon.Warning);
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

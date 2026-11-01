using System.Globalization;
using System.Windows.Threading;
using BleUnlock.Shared;
using BleUnlockManager.Core;
using BleUnlockManager.Core.Config;
using BleUnlockManager.Core.Localization;
using BleUnlockManager.Helpers;
using BleUnlockManager.Services;
using BleUnlockManager.Services.Tray;
using BleUnlockManager.ViewModels;
using BleUnlockManager.Views;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui;
using BleUnlockManager.Core.Resources;

namespace BleUnlockManager;

public partial class App : Application
{
    private const int TrayMaxRetries = 6;
    private static readonly TimeSpan TrayRetryInterval = TimeSpan.FromSeconds(5);

    private SingleInstanceGuard? _singleInstance;
    private ServiceProvider? _services;
    private TrayHost? _tray;
    private DispatcherTimer? _trayRetryTimer;
    private int _trayRetries;

    /// <summary>Contenedor de dependencias: páginas y modelos de vista se registran como únicos.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Segunda instancia: despierta a la primera (la guarda envía la señal) y termina
        _singleInstance = SingleInstanceGuard.TryCreate(OnWakeupRequested);
        if (_singleInstance is null)
        {
            Shutdown();
            return;
        }

        // Idioma antes de crear cualquier ventana: ajuste guardado en la configuración del servicio
        // o, si no hay (o es «system»), el idioma de Windows del usuario
        ManagerLanguage.Set(AppLanguages.Resolve(
            ConfigDocument.ReadLanguage(AppPaths.ConfigFile), CultureInfo.CurrentUICulture));

        _services = BuildServices();
        Services = _services;

        UiTheme.Initialize();

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();

        // Tema guardado en %LocalAppData% (compatible con la versión WinUI)
        _services.GetRequiredService<SettingsViewModel>().ApplySavedTheme();

        _ = _services.GetRequiredService<TargetDeviceService>().RefreshAsync();

        // El registro se sigue desde el arranque: la navegación muestra las advertencias del día
        _services.GetRequiredService<LogsViewModel>().Initialize();

        TryInitializeTray();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayRetryTimer?.Stop();
        _tray?.Dispose();
        _services?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<IContentDialogService, ContentDialogService>();
        services.AddSingleton<ServiceControlService>();
        services.AddSingleton<TargetDeviceService>();
        services.AddSingleton<TelemetryStore>();

        services.AddSingleton<MainWindow>();
        services.AddSingleton<OverviewViewModel>();
        services.AddSingleton<OverviewPage>();
        services.AddSingleton<ConfigViewModel>();
        services.AddSingleton<ConfigPage>();
        services.AddSingleton<LogsViewModel>();
        services.AddSingleton<LogsPage>();
        services.AddSingleton<RulesViewModel>();
        services.AddSingleton<RulesPage>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<SettingsPage>();

        return services.BuildServiceProvider();
    }

    // Al iniciar sesión el shell puede no estar listo: se reintenta cada 5 s hasta 6 veces.
    // Sin bandeja, cerrar la ventana termina la aplicación (no puede quedar invisible)
    private void TryInitializeTray()
    {
        if (MainWindow is not MainWindow window || _services is null)
        {
            return;
        }

        try
        {
            _tray = new TrayHost(window, _services.GetRequiredService<TelemetryStore>(),
                _services.GetRequiredService<ServiceControlService>());
            _tray.ExitRequested += (_, _) =>
            {
                _tray?.Dispose();
                _tray = null;
                window.ExitApplication();
            };
            window.TrayAvailable = true;
            _trayRetryTimer?.Stop();
        }
        catch
        {
            _tray = null;
            window.TrayAvailable = false;
            if (_trayRetries++ >= TrayMaxRetries)
            {
                return;
            }

            if (_trayRetryTimer is null)
            {
                _trayRetryTimer = new DispatcherTimer { Interval = TrayRetryInterval };
                _trayRetryTimer.Tick += (_, _) =>
                {
                    _trayRetryTimer.Stop();
                    TryInitializeTray();
                };
            }

            _trayRetryTimer.Start();
        }
    }

    // La señal de la otra instancia llega en un hilo de fondo
    private void OnWakeupRequested()
    {
        Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.ShowMainWindow());
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Un fallo de la interfaz no debe cerrar la bandeja: se informa y se continúa
        MessageBox.Show(e.Exception.Message, UiStrings.App_Title, MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

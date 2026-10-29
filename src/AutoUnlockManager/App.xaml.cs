using System.Windows.Threading;
using AutoUnlockManager.Helpers;
using AutoUnlockManager.Models;
using AutoUnlockManager.Services;
using AutoUnlockManager.ViewModels;
using AutoUnlockManager.Views;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui;

namespace AutoUnlockManager;

public partial class App : Application
{
    private SingleInstanceGuard? _singleInstance;
    private ServiceProvider? _services;

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

        _services = BuildServices();
        Services = _services;

        UiTheme.Initialize();

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();

        // La preferencia guardada se aplica en la tarea de ajustes; por ahora se sigue a Windows
        UiTheme.SetMode(AppThemeMode.System);

        _ = _services.GetRequiredService<TargetDeviceService>().RefreshAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
        services.AddSingleton<ConfigPage>();
        services.AddSingleton<LogsPage>();
        services.AddSingleton<SettingsPage>();

        return services.BuildServiceProvider();
    }

    // La señal de la otra instancia llega en un hilo de fondo
    private void OnWakeupRequested()
    {
        Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.ShowMainWindow());
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Un fallo de la interfaz no debe cerrar la bandeja: se informa y se continúa
        MessageBox.Show(e.Exception.Message, "AutoUnlock 管理器", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

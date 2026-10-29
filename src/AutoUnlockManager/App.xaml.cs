using System.Windows.Threading;
using AutoUnlockManager.Helpers;
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

    /// <summary>Contenedor de dependencias: p谩ginas y modelos de vista se registran como 煤nicos.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Segunda instancia: despierta a la primera (la guarda env铆a la se帽al) y termina
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

        // Tema guardado en %LocalAppData% (compatible con la versi贸n WinUI)
        _services.GetRequiredService<SettingsViewModel>().ApplySavedTheme();

        _ = _services.GetRequiredService<TargetDeviceService>().RefreshAsync();

        // El registro se sigue desde el arranque: la navegaci贸n muestra las advertencias del d铆a
        _services.GetRequiredService<LogsViewModel>().Initialize();
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
        services.AddSingleton<ConfigViewModel>();
        services.AddSingleton<ConfigPage>();
        services.AddSingleton<LogsViewModel>();
        services.AddSingleton<LogsPage>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<SettingsPage>();

        return services.BuildServiceProvider();
    }

    // La se帽al de la otra instancia llega en un hilo de fondo
    private void OnWakeupRequested()
    {
        Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.ShowMainWindow());
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Un fallo de la interfaz no debe cerrar la bandeja: se informa y se contin煤a
        MessageBox.Show(e.Exception.Message, "AutoUnlock 绠＄悊鍣?, MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

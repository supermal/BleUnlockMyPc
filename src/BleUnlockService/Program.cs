using System.Globalization;
using BleUnlock.Shared;
using BleUnlockService.Ble;
using BleUnlockService.Bonds;
using BleUnlockService.Config;
using BleUnlockService.Core;
using BleUnlockService.Credentials;
using BleUnlockService.Localization;
using BleUnlockService.Management;
using BleUnlockService.Notifications;
using BleUnlockService.Resources;
using BleUnlockService.Session;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

namespace BleUnlockService;

public static class Program
{
    // Carpeta unica para configuracion y registros, compartida con el instalador
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BleUnlock");

    public static void Main(string[] args)
    {
        Directory.CreateDirectory(Path.Combine(DataDir, "logs"));

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(DataDir, "logs", "bleunlock-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true)
            .WriteTo.EventLog("BleUnlock", manageEventSource: false,
                restrictedToMinimumLevel: LogEventLevel.Warning)
            .CreateLogger();

        try
        {
            // Los primeros mensajes salen antes de que exista ServiceLanguage: idioma del ajuste
            // guardado, o del equipo; el asistente lo afinará al conectarse
            ServiceStrings.Culture = AppLanguages.ToCulture(AppLanguages.Resolve(
                ReadLanguageSetting(), CultureInfo.InstalledUICulture));

            Log.Information(ServiceStrings.Log_ServiceStarting);

            var builder = Host.CreateApplicationBuilder(args);
            builder.Configuration.AddJsonFile(
                Path.Combine(DataDir, "appsettings.json"), optional: false, reloadOnChange: true);

            // Validacion temprana: fallar aqui evita que el SCM marque el servicio como
            // "en ejecucion" y lo reinicie en bucle por el plan de recuperacion (sc failure)
            var initialOptions = builder.Configuration
                .GetSection(BleUnlockOptions.SectionName).Get<BleUnlockOptions>() ?? new BleUnlockOptions();
            if (!TryResolveSecrets(initialOptions))
            {
                Environment.ExitCode = 1;
                return;
            }

            var earlyValidation = OptionsValidator.Validate(initialOptions);
            if (!earlyValidation.IsValid)
            {
                foreach (var error in earlyValidation.Errors)
                    Log.Fatal(ServiceStrings.Log_ConfigInvalidRefuseStart, error);
                Environment.ExitCode = 1;
                return;
            }

            builder.Services.AddWindowsService(o => o.ServiceName = "BleUnlockService");
            builder.Services.AddSerilog();
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.Configure<BleUnlockOptions>(
                builder.Configuration.GetSection(BleUnlockOptions.SectionName));

            // Sustituye al ciclo de vida que registra AddWindowsService, para recibir
            // los avisos de bloqueo, desbloqueo y reanudacion. Solo cuando corre como
            // servicio: en consola se conserva ConsoleLifetime, como haria AddWindowsService
            builder.Services.AddSingleton<SessionMonitor>();
            if (WindowsServiceHelpers.IsWindowsService())
                builder.Services.AddSingleton<IHostLifetime>(sp => sp.GetRequiredService<SessionMonitor>());

            builder.Services.AddSingleton(sp =>
            {
                var initial = sp.GetRequiredService<IOptionsMonitor<BleUnlockOptions>>().CurrentValue;
                if (!TryResolveSecrets(initial))
                {
                    throw new InvalidOperationException(ServiceStrings.Error_ConfigInvalid);
                }

                var validation = OptionsValidator.Validate(initial);
                if (!validation.IsValid)
                {
                    foreach (var error in validation.Errors)
                        Log.Fatal(ServiceStrings.Log_ConfigInvalidRefuseStart, error);
                    throw new InvalidOperationException(ServiceStrings.Error_ConfigInvalid);
                }

                var gate = new OptionsGate(
                    initial, sp.GetRequiredService<ILogger<OptionsGate>>(),
                    sp.GetRequiredService<TimeProvider>(), TimeSpan.FromMilliseconds(250),
                    Path.Combine(DataDir, "reload-status.json"));

                sp.GetRequiredService<IOptionsMonitor<BleUnlockOptions>>()
                  .OnChange(candidate => gate.Submit(candidate));

                return gate;
            });

            builder.Services.AddSingleton(sp => new ServiceLanguage(
                sp.GetRequiredService<OptionsGate>(), CultureInfo.InstalledUICulture));
            builder.Services.AddSingleton<IRssiSource, BleScanner>();
            builder.Services.AddSingleton<DeviceMatcher>();
            builder.Services.AddSingleton<CredentialValidator>();
            builder.Services.AddSingleton<TelemetryHub>();
            builder.Services.AddSingleton<CredentialPipeServer>();
            builder.Services.AddSingleton<HelperPipeServer>();
            builder.Services.AddSingleton<HelperLauncher>();
            builder.Services.AddSingleton<HelperNotifier>();
            builder.Services.AddSingleton<WtsMessageNotifier>();
            builder.Services.AddSingleton<ProcessScreenLocker>();

            builder.Services.AddSingleton(sp => new ResilientNotifier(
                sp.GetRequiredService<HelperNotifier>(),
                sp.GetRequiredService<WtsMessageNotifier>(),
                sp.GetRequiredService<ILogger<ResilientNotifier>>()));

            builder.Services.AddSingleton<IScreenLocker>(sp => new HelperScreenLocker(
                sp.GetRequiredService<HelperPipeServer>(),
                sp.GetRequiredService<ProcessScreenLocker>(),
                sp.GetRequiredService<ILogger<HelperScreenLocker>>()));

            builder.Services.AddSingleton<IBondedDeviceSource, WindowsBondedDeviceSource>();

            builder.Services.AddHostedService<HelperLanguageRelay>();
            builder.Services.AddHostedService<Worker>();
            builder.Services.AddHostedService<ManagementPipeServer>();
            builder.Services.AddHostedService<BondPipeServer>();

            builder.Build().Run();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, ServiceStrings.Log_ServiceStartFailed);
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    // 敏感值（Device.Irk / Credentials.Password）解析：dpapi 密文 → 明文；
    // 明文记警告（管理端保存时自动加密）；密文解不开按配置无效处理（SCM 不进入运行态）
    // Lectura mínima e indulgente: si el fichero falta o está dañado se sigue al sistema
    private static string? ReadLanguageSetting()
    {
        try
        {
            return new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(DataDir, "appsettings.json"), optional: true)
                .Build()[$"{BleUnlockOptions.SectionName}:Ui:Language"];
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool TryResolveSecrets(BleUnlockOptions options)
    {
        OptionsSecrets.TryResolve(options, out var warnings, out var errors);
        foreach (var warning in warnings)
            Log.Warning("{Warning}", warning);
        foreach (var error in errors)
            Log.Fatal(ServiceStrings.Log_ConfigInvalidRefuseStart, error);
        return errors.Count == 0;
    }
}

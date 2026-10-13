using Serilog;
using Serilog.Events;

namespace AutoUnlockService;

public static class Program
{
    // Carpeta unica para configuracion y registros, compartida con el instalador
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AutoUnlock");

    public static void Main(string[] args)
    {
        Directory.CreateDirectory(Path.Combine(DataDir, "logs"));

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(DataDir, "logs", "autounlock-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true)
            .WriteTo.EventLog("AutoUnlock", manageEventSource: false,
                restrictedToMinimumLevel: LogEventLevel.Warning)
            .CreateLogger();

        try
        {
            Log.Information("AutoUnlockService 正在启动");

            var builder = Host.CreateApplicationBuilder(args);
            builder.Configuration.AddJsonFile(
                Path.Combine(DataDir, "appsettings.json"), optional: false, reloadOnChange: true);
            builder.Services.AddWindowsService(o => o.ServiceName = "AutoUnlockService");
            builder.Services.AddSerilog();
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddHostedService<Worker>();

            builder.Build().Run();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "AutoUnlockService 启动失败");
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}

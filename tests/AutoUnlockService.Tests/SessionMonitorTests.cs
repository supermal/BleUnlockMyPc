using System.Reflection;
using System.ServiceProcess;
using AutoUnlockService.Session;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AutoUnlockService.Tests;

public class SessionMonitorTests
{
    private sealed class FakeEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private static SessionMonitor NewMonitor() => new(
        new FakeEnvironment(), new FakeLifetime(), NullLoggerFactory.Instance,
        Options.Create(new HostOptions()), Options.Create(new WindowsServiceLifetimeOptions { ServiceName = "AutoUnlockService" }));

    private static void InvokeProtected(SessionMonitor monitor, string method, object arg) =>
        typeof(SessionMonitor).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(monitor, [arg]);

    private static SessionChangeDescription Change(SessionChangeReason reason) =>
        (SessionChangeDescription)Activator.CreateInstance(
            typeof(SessionChangeDescription),
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            null, [reason, 1], null)!;

    [Fact]
    public void El_constructor_activa_las_banderas_que_ServiceBase_lee_antes_de_arrancar()
    {
        using var monitor = NewMonitor();

        Assert.True(monitor.CanHandleSessionChangeEvent);
        Assert.True(monitor.CanHandlePowerEvent);
    }

    [Theory]
    [InlineData(SessionChangeReason.SessionLock, 1, 0)]
    [InlineData(SessionChangeReason.SessionUnlock, 0, 1)]
    [InlineData(SessionChangeReason.SessionLogon, 0, 1)]
    [InlineData(SessionChangeReason.RemoteConnect, 0, 0)]
    public void Los_cambios_de_sesion_se_traducen_en_eventos(SessionChangeReason reason, int locks, int unlocks)
    {
        using var monitor = NewMonitor();
        int lockCount = 0, unlockCount = 0;
        monitor.SessionLocked += () => lockCount++;
        monitor.SessionUnlocked += () => unlockCount++;

        InvokeProtected(monitor, "OnSessionChange", Change(reason));

        Assert.Equal(locks, lockCount);
        Assert.Equal(unlocks, unlockCount);
    }

    [Theory]
    [InlineData(PowerBroadcastStatus.ResumeSuspend, 1)]
    [InlineData(PowerBroadcastStatus.ResumeAutomatic, 1)]
    [InlineData(PowerBroadcastStatus.Suspend, 0)]
    public void La_reanudacion_dispara_Resumed_y_la_suspension_no(PowerBroadcastStatus status, int resumes)
    {
        using var monitor = NewMonitor();
        int count = 0;
        monitor.Resumed += () => count++;

        InvokeProtected(monitor, "OnPowerEvent", status);

        Assert.Equal(resumes, count);
    }

    [Fact]
    public void Un_suscriptor_que_falla_no_propaga_la_excepcion_al_SCM()
    {
        using var monitor = NewMonitor();
        monitor.SessionLocked += () => throw new InvalidOperationException("fallo");

        InvokeProtected(monitor, "OnSessionChange", Change(SessionChangeReason.SessionLock));
    }
}

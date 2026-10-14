using System.ServiceProcess;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

namespace AutoUnlockService.Session;

// Sustituye al ciclo de vida por defecto para poder pedirle al SCM los avisos de
// bloqueo, desbloqueo y reanudacion tras suspension
public sealed class SessionMonitor : WindowsServiceLifetime
{
    private readonly ILogger _log;

    public SessionMonitor(
        IHostEnvironment environment,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory,
        IOptions<HostOptions> optionsAccessor,
        IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptionsAccessor)
    {
        // ServiceBase lee estas banderas al registrar el servicio, antes de OnStart:
        // asignarlas mas tarde no tendria efecto y el servicio nunca recibiria los avisos
        CanHandleSessionChangeEvent = true;
        CanHandlePowerEvent = true;
        _log = loggerFactory.CreateLogger<SessionMonitor>();
    }

    public event Action? SessionLocked;
    public event Action? SessionUnlocked;
    public event Action? Resumed;

    protected override void OnSessionChange(SessionChangeDescription changeDescription)
    {
        base.OnSessionChange(changeDescription);

        switch (changeDescription.Reason)
        {
            case SessionChangeReason.SessionLock:
                Raise(SessionLocked);
                break;
            case SessionChangeReason.SessionUnlock:
            case SessionChangeReason.SessionLogon:
                Raise(SessionUnlocked);
                break;
        }
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        // Tras despertar el reloj puede haber saltado horas: el historico ya no sirve
        if (powerStatus is PowerBroadcastStatus.ResumeSuspend or PowerBroadcastStatus.ResumeAutomatic)
            Raise(Resumed);

        return base.OnPowerEvent(powerStatus);
    }

    // Se ejecuta en un hilo del SCM: un suscriptor que falle no debe tumbar la notificacion
    private void Raise(Action? handler)
    {
        try { handler?.Invoke(); }
        catch (Exception ex) { _log.LogError(ex, "处理会话或电源事件时出错"); }
    }
}

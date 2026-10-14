namespace AutoUnlockService.Session;

public sealed class HelperScreenLocker(
    HelperPipeServer pipe, ProcessScreenLocker fallback, ILogger<HelperScreenLocker> log) : IScreenLocker
{
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(5);

    public async Task<bool> LockAsync(CancellationToken ct)
    {
        if (pipe.IsConnected
            && await pipe.SendAsync(HelperProtocol.BuildLock(), ct)
            && await pipe.WaitForLockedAsync(AckTimeout, ct))
        {
            log.LogInformation("助手已执行锁屏");
            return true;
        }

        // Bloquear la pantalla es lo mas importante del servicio: si el ayudante
        // no confirma, se intenta por la via directa antes de darse por vencido
        log.LogWarning("助手未确认锁屏，改用进程方式");
        return await fallback.LockAsync(ct);
    }
}

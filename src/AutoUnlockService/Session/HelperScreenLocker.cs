namespace AutoUnlockService.Session;

// Lo que HelperScreenLocker necesita del canal con el ayudante
public interface IHelperCommandChannel
{
    bool IsConnected { get; }
    Task<bool> SendAsync(string line, CancellationToken ct);
    Task<bool> WaitForLockedAsync(TimeSpan timeout, CancellationToken ct);
}

public sealed class HelperScreenLocker : IScreenLocker
{
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(5);

    private readonly IHelperCommandChannel _pipe;
    private readonly IScreenLocker _fallback;
    private readonly ILogger _log;

    public HelperScreenLocker(HelperPipeServer pipe, ProcessScreenLocker fallback, ILogger<HelperScreenLocker> log)
        : this((IHelperCommandChannel)pipe, fallback, log) { }

    internal HelperScreenLocker(IHelperCommandChannel pipe, IScreenLocker fallback, ILogger log)
    {
        _pipe = pipe;
        _fallback = fallback;
        _log = log;
    }

    public async Task<bool> LockAsync(CancellationToken ct)
    {
        if (_pipe.IsConnected
            && await _pipe.SendAsync(HelperProtocol.BuildLock(), ct)
            && await _pipe.WaitForLockedAsync(AckTimeout, ct))
        {
            _log.LogInformation("助手已执行锁屏");
            return true;
        }

        // Bloquear la pantalla es lo mas importante del servicio: si el ayudante
        // no confirma, se intenta por la via directa antes de darse por vencido
        _log.LogWarning("助手未确认锁屏，改用进程方式");
        return await _fallback.LockAsync(ct);
    }
}

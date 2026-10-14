namespace AutoUnlockService.Session;

// Respaldo de bloqueo cuando el ayudante no esta disponible
public sealed class ProcessScreenLocker(ILogger<ProcessScreenLocker> log) : IScreenLocker
{
    public Task<bool> LockAsync(CancellationToken ct)
    {
        // Ruta entre comillas: CreateProcessAsUser analiza la linea de comandos y una ruta con espacios la rompe
        var command = $"\"{Environment.SystemDirectory}\rundll32.exe\" user32.dll,LockWorkStation";
        bool ok = UserSessionProcess.StartInActiveSession(command, log) is not null;
        if (ok) log.LogInformation("已通过 rundll32 在用户会话中锁屏");
        return Task.FromResult(ok);
    }
}

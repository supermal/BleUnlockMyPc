namespace AutoUnlockService.Session;

// Respaldo de bloqueo cuando el ayudante no esta disponible
public sealed class ProcessScreenLocker(ILogger<ProcessScreenLocker> log) : IScreenLocker
{
    // Path.Combine y no barras en una cadena interpolada: barra seguida de r seria un retorno de carro
    // y el comando apuntaria a un fichero inexistente sin ningun aviso del compilador
    internal static string BuildCommand() =>
        $"\"{Path.Combine(Environment.SystemDirectory, "rundll32.exe")}\" user32.dll,LockWorkStation";

    public Task<bool> LockAsync(CancellationToken ct)
    {
        bool ok = UserSessionProcess.StartInActiveSession(BuildCommand(), log) is not null;
        if (ok) log.LogInformation("已通过 rundll32 在用户会话中锁屏");
        else log.LogError("通过 rundll32 锁屏失败：屏幕未被锁定");
        return Task.FromResult(ok);
    }
}

using AutoUnlockService.Resources;

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
        if (ok) log.LogInformation(ServiceStrings.Log_Rundll32Locked);
        else log.LogError(ServiceStrings.Log_Rundll32LockFailed);
        return Task.FromResult(ok);
    }
}

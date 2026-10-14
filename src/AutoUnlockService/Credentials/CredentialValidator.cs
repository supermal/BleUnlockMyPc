using System.Runtime.InteropServices;

namespace AutoUnlockService.Credentials;

// Prevalidacion: comprueba usuario y clave antes de que haga falta desbloquear.
// Usa LOGON32_LOGON_NETWORK, que no crea sesion interactiva ni perfil de usuario
public sealed class CredentialValidator(ILogger<CredentialValidator> log)
{
    private const int Logon32LogonNetwork = 3;
    private const int Logon32ProviderDefault = 0;

    public bool Validate(string userName, string password)
    {
        var name = UserNameParser.Split(userName);

        if (!LogonUserW(name.User, name.Domain, password,
                Logon32LogonNetwork, Logon32ProviderDefault, out var token))
        {
            int error = Marshal.GetLastWin32Error();
            // Jamas se registra la clave, solo el usuario y el codigo de error
            log.LogError("凭据预验证失败，用户 {User}，错误码 {Error}", userName, error);
            return false;
        }

        token.Dispose();
        log.LogInformation("凭据预验证通过，用户 {User}", userName);
        return true;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "LogonUserW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUserW(
        string lpszUsername,
        string? lpszDomain,
        string lpszPassword,
        int dwLogonType,
        int dwLogonProvider,
        out Microsoft.Win32.SafeHandles.SafeAccessTokenHandle phToken);
}

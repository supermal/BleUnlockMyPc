using System.Runtime.InteropServices;
using BleUnlockService.Resources;

namespace BleUnlockService.Credentials;

public enum CredentialCheck
{
    Valid,
    Invalid,     // Windows dice que usuario o clave estan mal: se suspende
    Unverified   // no se pudo comprobar (sin controlador de dominio, sin derecho de inicio por red)
}

// Prevalidacion: comprueba usuario y clave antes de que haga falta desbloquear.
// Primero LOGON32_LOGON_NETWORK, que no crea sesion interactiva ni perfil de usuario
public sealed class CredentialValidator(ILogger<CredentialValidator> log)
{
    internal const int Logon32LogonInteractive = 2;
    internal const int Logon32LogonNetwork = 3;
    private const int Logon32ProviderDefault = 0;

    private const int ErrorLogonFailure = 1326;
    private const int ErrorLogonTypeNotGranted = 1385;

    // Fallos que no dicen nada de la clave: el equipo no puede preguntar ahora mismo al dominio
    private static readonly HashSet<int> TransientErrors =
    [
        1311,   // ERROR_NO_LOGON_SERVERS
        1355,   // ERROR_NO_SUCH_DOMAIN
        1722,   // RPC_S_SERVER_UNAVAILABLE
        1786,   // ERROR_NO_TRUST_LSA_SECRET
        1787,   // ERROR_NO_TRUST_SAM_ACCOUNT
        1788,   // ERROR_TRUSTED_DOMAIN_FAILURE
        1789,   // ERROR_TRUSTED_RELATIONSHIP_FAILURE
        1790,   // ERROR_TRUST_FAILURE
    ];

    public CredentialCheck Validate(string userName, string password)
    {
        var name = UserNameParser.Split(userName);
        var (check, error) = Decide(
            logonType => TryLogon(name, password, logonType),
            IsMicrosoftAccount(name));

        // Jamas se registra la clave, solo el usuario y el codigo de error
        switch (check)
        {
            case CredentialCheck.Valid:
                log.LogInformation(ServiceStrings.Log_CredPrecheckPassed, userName);
                break;
            case CredentialCheck.Unverified:
                log.LogWarning(ServiceStrings.Log_CredPrecheckUnverified, userName, error);
                break;
            default:
                log.LogError(ServiceStrings.Log_CredPrecheckFailed, userName, error);
                break;
        }

        return check;
    }

    // attempt(tipo) devuelve 0 si el inicio funciono o el codigo Win32 del fallo.
    //  - Directivas de dominio que niegan «acceso desde la red» (1385): se repite como interactivo.
    //  - Cuenta Microsoft con 1326: segundo intento interactivo, por si el inicio por red no la admite.
    //    Cuesta un intento fallido mas solo cuando la clave es de verdad incorrecta
    internal static (CredentialCheck Check, int Error) Decide(Func<int, int> attempt, bool microsoftAccount)
    {
        int error = attempt(Logon32LogonNetwork);
        if (error == 0) return (CredentialCheck.Valid, 0);

        if (error == ErrorLogonTypeNotGranted || (error == ErrorLogonFailure && microsoftAccount))
        {
            int retry = attempt(Logon32LogonInteractive);
            if (retry == 0) return (CredentialCheck.Valid, 0);
            error = retry;
        }

        if (error == ErrorLogonTypeNotGranted || TransientErrors.Contains(error))
            return (CredentialCheck.Unverified, error);
        return (CredentialCheck.Invalid, error);
    }

    // «MicrosoftAccount\correo», o un correo suelto sin dominio (tambien podria ser el UPN de un
    // dominio; en ese caso el reintento solo cuesta un intento mas si la clave esta mal)
    internal static bool IsMicrosoftAccount(LogonName name) =>
        string.Equals(name.Domain, "MicrosoftAccount", StringComparison.OrdinalIgnoreCase) ||
        (name.Domain is null && name.User.Contains('@'));

    private static int TryLogon(LogonName name, string password, int logonType)
    {
        if (LogonUserW(name.User, name.Domain, password, logonType, Logon32ProviderDefault, out var token))
        {
            token.Dispose();
            return 0;
        }

        int error = Marshal.GetLastWin32Error();
        return error == 0 ? ErrorLogonFailure : error;
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

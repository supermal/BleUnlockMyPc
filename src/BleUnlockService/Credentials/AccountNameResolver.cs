using System.Security.Principal;

namespace BleUnlockService.Credentials;

// Convierte lo que el usuario escribio en el nombre canonico «DOMINIO\usuario» que Windows asocia
// a la cuenta: «Admin» → «EQUIPO\Admin», «hq.qiu@kirol.es» → «KIROL\hq.qiu», el correo de una
// cuenta Microsoft → «MicrosoftAccount\correo». Se hace aqui (SYSTEM, sin prisa) y no en el CP,
// que corre dentro de LogonUI y no debe esperar a la red
public static class AccountNameResolver
{
    // null si no se pudo resolver (p. ej. sin controlador de dominio): se usa el nombre tal cual
    public static string? TryResolve(string userName, out string? error, Func<string, string>? lookup = null)
        => TryResolve(userName, out error, out _, lookup);

    // notFound: Windows no conoce la cuenta (p. ej. un correo de Outlook no vinculado a este equipo).
    // Sin controlador de dominio tambien puede salir asi; por eso solo se usa junto a un rechazo de LogonUser
    public static string? TryResolve(string userName, out string? error, out bool notFound,
        Func<string, string>? lookup = null)
    {
        error = null;
        notFound = false;
        var query = Normalize(userName);
        if (query.Length == 0) return null;

        try
        {
            var resolved = (lookup ?? Lookup)(query);
            return string.IsNullOrWhiteSpace(resolved) ? null : resolved;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            error = ex.Message;
            notFound = ex is IdentityNotMappedException;
            return null;
        }
    }

    // NTAccount no entiende «.\usuario» ni «\usuario»: el punto es este equipo
    internal static string Normalize(string userName)
    {
        var trimmed = userName.Trim();
        if (trimmed.StartsWith(".\\", StringComparison.Ordinal)) return Environment.MachineName + trimmed[1..];
        if (trimmed.StartsWith('\\')) return Environment.MachineName + trimmed;
        return trimmed;
    }

    // Ida y vuelta por el SID: LookupAccountName acepta las tres formas y LookupAccountSid
    // devuelve siempre el nombre canonico
    private static string Lookup(string name)
    {
        var sid = (SecurityIdentifier)new NTAccount(name).Translate(typeof(SecurityIdentifier));
        return ((NTAccount)sid.Translate(typeof(NTAccount))).Value;
    }
}

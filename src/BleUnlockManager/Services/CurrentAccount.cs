using System.Security.Principal;
using BleUnlockManager.Core.Config;

namespace BleUnlockManager.Services;

/// <summary>
/// Nombre de inicio de sesión de quien usa el Gestor, ya en la forma que entiende Windows:
/// «EQUIPO\usuario» (local), «DOMINIO\usuario» (dominio) o «MicrosoftAccount\correo» (cuenta Microsoft).
/// El asistente lo propone en vez de <see cref="Environment.UserName"/>, que pierde el dominio y,
/// en una cuenta Microsoft, da un alias local que no sirve para iniciar sesión.
/// </summary>
public static class CurrentAccount
{
    // Autoridad de las cuentas Microsoft: el token de una sesión iniciada con ellas lleva este grupo
    private const string MicrosoftAccountSidPrefix = "S-1-11-96-";

    public static string DefaultLogonName()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return DefaultLogonName(identity);
        }
        catch (SystemException)
        {
            return Environment.UserName;
        }
    }

    /// <summary>
    /// ¿El usuario escrito es la cuenta de esta sesión? Consulta a Windows (puede tardar con un dominio):
    /// llamarlo fuera del hilo de la interfaz.
    /// </summary>
    public static AccountMatch Check(string userName)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var current = DefaultLogonName(identity);

            SecurityIdentifier sid;
            try
            {
                sid = (SecurityIdentifier)new NTAccount(Normalize(userName)).Translate(typeof(SecurityIdentifier));
            }
            catch (IdentityNotMappedException)
            {
                return new AccountMatch(AccountMatchKind.NotFound, current);
            }

            // Una cuenta Microsoft se resuelve a su SID S-1-11-96-…, que la sesión lleva como grupo
            // identity.User es null en identidades anónimas: entonces solo cuenta la comparación por grupo
            bool same = (identity.User is { } user && sid.Equals(user)) ||
                (sid.Value.StartsWith(MicrosoftAccountSidPrefix, StringComparison.Ordinal) &&
                 (identity.Groups?.Any(g => g.Equals(sid)) ?? false));
            return new AccountMatch(same ? AccountMatchKind.Same : AccountMatchKind.Other, current);
        }
        catch (SystemException)
        {
            return new AccountMatch(AccountMatchKind.Unknown, "");
        }
    }

    private static string DefaultLogonName(WindowsIdentity identity)
    {
        var microsoftAccount = identity.Groups?
            .FirstOrDefault(sid => sid.Value.StartsWith(MicrosoftAccountSidPrefix, StringComparison.Ordinal));
        if (microsoftAccount is not null && TryTranslate(microsoftAccount) is { } email)
        {
            return email;
        }

        return identity.Name;
    }

    // NTAccount no entiende «.\usuario» ni «\usuario»: el punto es este equipo
    private static string Normalize(string userName)
    {
        var trimmed = userName.Trim();
        if (trimmed.StartsWith(".\\", StringComparison.Ordinal)) return Environment.MachineName + trimmed[1..];
        if (trimmed.StartsWith('\\')) return Environment.MachineName + trimmed;
        return trimmed;
    }

    private static string? TryTranslate(IdentityReference sid)
    {
        try
        {
            var name = ((NTAccount)sid.Translate(typeof(NTAccount))).Value;
            return name.Length > 0 ? name : null;
        }
        catch (SystemException)
        {
            return null;
        }
    }
}

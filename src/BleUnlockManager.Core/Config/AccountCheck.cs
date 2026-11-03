using System.Globalization;
using BleUnlockManager.Core.Resources;

namespace BleUnlockManager.Core.Config;

/// <summary>Cómo se relaciona el usuario escrito con la cuenta que tiene abierta la sesión de Windows.</summary>
public enum AccountMatchKind
{
    /// <summary>Es la cuenta de la sesión actual: puede desbloquearla.</summary>
    Same,

    /// <summary>Existe, pero es otra cuenta: no puede desbloquear la sesión actual.</summary>
    Other,

    /// <summary>Windows no conoce esa cuenta en este equipo (p. ej. un correo de Outlook no vinculado).</summary>
    NotFound,

    /// <summary>No se pudo comprobar (sin controlador de dominio, error inesperado): no se avisa.</summary>
    Unknown,
}

/// <param name="CurrentAccount">Cuenta de la sesión actual, en la forma que propone el asistente.</param>
public sealed record AccountMatch(AccountMatchKind Kind, string CurrentAccount);

/// <summary>
/// Aviso al guardar (2026-11-03): tras bloquear, Windows solo deja desbloquear con la cuenta que
/// bloqueó. Un usuario de otra cuenta, o uno que no existe en el equipo, nunca desbloqueará la sesión.
/// </summary>
public static class AccountCheck
{
    /// <summary>Texto del aviso, o null si no hay nada que avisar.</summary>
    public static string? Warning(string enteredUserName, AccountMatch match) => match.Kind switch
    {
        AccountMatchKind.NotFound => string.Format(CultureInfo.CurrentCulture,
            UiStrings.Account_NotFound, enteredUserName.Trim(), match.CurrentAccount),
        AccountMatchKind.Other => string.Format(CultureInfo.CurrentCulture,
            UiStrings.Account_OtherUser, enteredUserName.Trim(), match.CurrentAccount),
        _ => null,
    };

    /// <summary>Solo se comprueba si el guardado toca usuario o contraseña y hay un usuario escrito.</summary>
    public static bool ShouldCheck(string userName, IEnumerable<ConfigChange> changes) =>
        userName.Trim().Length > 0 &&
        changes.Any(c => c.Field is ConfigFields.CredentialsUserName or ConfigFields.CredentialsPassword);
}

using System.Globalization;
using BleUnlockService.Resources;

namespace BleUnlockService.Credentials;

// Decide si un fallo de autenticacion significa "la credencial del fichero esta mal".
// Solo esos casos paran el servicio; el resto se reintenta
public static class NtStatusPolicy
{
    public const uint StatusLogonFailure      = 0xC000006D;
    public const uint StatusPasswordExpired   = 0xC0000071;
    public const uint StatusPasswordMustChange = 0xC0000224;
    public const uint StatusAccountLockedOut  = 0xC0000234;

    public static bool ShouldSuspend(uint ntStatus) => ntStatus is
        StatusLogonFailure or
        StatusPasswordExpired or
        StatusPasswordMustChange or
        StatusAccountLockedOut;

    public static string Describe(uint ntStatus) => ntStatus switch
    {
        StatusLogonFailure       => ServiceStrings.Auth_LogonFailure,
        StatusPasswordExpired    => ServiceStrings.Auth_PasswordExpired,
        StatusPasswordMustChange => ServiceStrings.Auth_PasswordMustChange,
        StatusAccountLockedOut   => ServiceStrings.Auth_AccountLockedOut,
        _ => string.Format(ServiceStrings.Auth_LogonFailedStatus, ntStatus.ToString("X8", CultureInfo.InvariantCulture))
    };
}

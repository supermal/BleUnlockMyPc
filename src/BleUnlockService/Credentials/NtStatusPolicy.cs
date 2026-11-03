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

    // Subestados habituales de un C000006D (y algunos que tambien llegan como estado principal)
    public const uint StatusNoSuchUser        = 0xC0000064;
    public const uint StatusWrongPassword     = 0xC000006A;
    public const uint StatusLogonHours        = 0xC000006F;
    public const uint StatusWorkstation       = 0xC0000070;
    public const uint StatusAccountDisabled   = 0xC0000072;
    public const uint StatusLogonTypeNotGranted = 0xC000015B;
    public const uint StatusAccountExpired    = 0xC0000193;

    public static bool ShouldSuspend(uint ntStatus) => ntStatus is
        StatusLogonFailure or
        StatusPasswordExpired or
        StatusPasswordMustChange or
        StatusAccountLockedOut;

    // Con subestado se explica la causa concreta; si no se reconoce, se describe el estado principal
    public static string Describe(uint ntStatus, uint substatus = 0)
    {
        if (ntStatus == StatusLogonFailure && DescribeKnown(substatus) is { } detail)
            return detail;

        return DescribeKnown(ntStatus) switch
        {
            { } known => known,
            null when ntStatus == StatusLogonFailure => ServiceStrings.Auth_LogonFailure,
            _ => string.Format(ServiceStrings.Auth_LogonFailedStatus, ntStatus.ToString("X8", CultureInfo.InvariantCulture))
        };
    }

    private static string? DescribeKnown(uint status) => status switch
    {
        StatusPasswordExpired     => ServiceStrings.Auth_PasswordExpired,
        StatusPasswordMustChange  => ServiceStrings.Auth_PasswordMustChange,
        StatusAccountLockedOut    => ServiceStrings.Auth_AccountLockedOut,
        StatusWrongPassword       => ServiceStrings.Auth_WrongPassword,
        StatusNoSuchUser          => ServiceStrings.Auth_NoSuchUser,
        StatusLogonHours          => ServiceStrings.Auth_LogonHours,
        StatusWorkstation         => ServiceStrings.Auth_Workstation,
        StatusAccountDisabled     => ServiceStrings.Auth_AccountDisabled,
        StatusAccountExpired      => ServiceStrings.Auth_AccountExpired,
        StatusLogonTypeNotGranted => ServiceStrings.Auth_LogonTypeNotGranted,
        _ => null
    };
}

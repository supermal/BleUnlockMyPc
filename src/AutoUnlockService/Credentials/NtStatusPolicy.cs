using System.Globalization;

namespace AutoUnlockService.Credentials;

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
        StatusLogonFailure       => "用户名或密码错误",
        StatusPasswordExpired    => "密码已过期",
        StatusPasswordMustChange => "密码必须修改后才能登录",
        StatusAccountLockedOut   => "账户已被锁定",
        _ => $"登录失败（NTSTATUS 0x{ntStatus.ToString("X8", CultureInfo.InvariantCulture)}）"
    };
}

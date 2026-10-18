namespace AutoUnlockService.Config;

public sealed class AutoUnlockOptions
{
    public const string SectionName = "AutoUnlock";

    public DeviceOptions Device { get; set; } = new();
    public CredentialOptions Credentials { get; set; } = new();
    public ProximityOptions Proximity { get; set; } = new();
    public NotificationOptions Notification { get; set; } = new();
}

public enum IrkByteOrder { Auto, Normal, Reversed }

public enum NotifierMode { Toast, Dialog }

public sealed class DeviceOptions
{
    public string Irk { get; set; } = "";
    public IrkByteOrder IrkByteOrder { get; set; } = IrkByteOrder.Auto;
    public string? IdentityAddress { get; set; }
}

public sealed class CredentialOptions
{
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class ProximityOptions
{
    public double LockThresholdDbm { get; set; } = -80;
    public double UnlockThresholdDbm { get; set; } = -45;

    // 判定改为批次全数达标（2026-10-18 改版）：触发读数不计入，之后每条读数都需达标；任一条不达标整批作废
    public int UnlockSampleCount { get; set; } = 3;          // 靠近确认：解锁
    public int LockPromptSampleCount { get; set; } = 5;      // 离开确认：弹窗（也是 Win+L 后的待命判定）
    public int LockConfirmSampleCount { get; set; } = 10;    // 弹窗后锁屏确认

    public double StartupGraceSeconds { get; set; } = 15;
    public double UnlockPendingTimeoutSeconds { get; set; } = 20;
}

public sealed class NotificationOptions
{
    public NotifierMode Mode { get; set; } = NotifierMode.Toast;
    public double RejectSnoozeSeconds { get; set; } = 60;
    public double SnoozeMinutes { get; set; } = 5;
}

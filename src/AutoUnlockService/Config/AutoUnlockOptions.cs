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
    public double LockWindowSeconds { get; set; } = 10;
    public double UnlockThresholdDbm { get; set; } = -45;
    public double UnlockWindowSeconds { get; set; } = 5;
    public int MinSamples { get; set; } = 3;
    public double StartupGraceSeconds { get; set; } = 15;
    public double UnlockPendingTimeoutSeconds { get; set; } = 20;
}

public sealed class NotificationOptions
{
    public NotifierMode Mode { get; set; } = NotifierMode.Toast;
    public double PromptTimeoutSeconds { get; set; } = 15;
    public double RejectSnoozeSeconds { get; set; } = 60;
    public double SnoozeMinutes { get; set; } = 5;
}

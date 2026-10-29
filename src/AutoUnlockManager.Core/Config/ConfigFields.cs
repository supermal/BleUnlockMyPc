namespace AutoUnlockManager.Core.Config;

/// <summary>
/// 配置字段点号标识（与服务端 <c>OptionsValidator</c> 消息前缀一致，UI 错误映射与测试断言共用）。
/// </summary>
public static class ConfigFields
{
    public const string DeviceIrk = "Device.Irk";
    public const string DeviceIrkByteOrder = "Device.IrkByteOrder";
    public const string DeviceIdentityAddress = "Device.IdentityAddress";
    public const string CredentialsUserName = "Credentials.UserName";
    public const string CredentialsPassword = "Credentials.Password";
    public const string ProximityLockThresholdDbm = "Proximity.LockThresholdDbm";
    public const string ProximityUnlockThresholdDbm = "Proximity.UnlockThresholdDbm";
    public const string ProximityUnlockSampleCount = "Proximity.UnlockSampleCount";
    public const string ProximityLockPromptSampleCount = "Proximity.LockPromptSampleCount";
    public const string ProximityLockConfirmSampleCount = "Proximity.LockConfirmSampleCount";
    public const string ProximitySampleIntervalMs = "Proximity.SampleIntervalMs";
    public const string ProximityStartupGraceSeconds = "Proximity.StartupGraceSeconds";
    public const string ProximityUnlockPendingTimeoutSeconds = "Proximity.UnlockPendingTimeoutSeconds";
    public const string NotificationMode = "Notification.Mode";
    public const string NotificationRejectSnoozeSeconds = "Notification.RejectSnoozeSeconds";
    public const string NotificationSnoozeMinutes = "Notification.SnoozeMinutes";
}

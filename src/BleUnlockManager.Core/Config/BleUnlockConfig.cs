namespace BleUnlockManager.Core.Config;

/// <summary>
/// 服务配置模型（镜像服务端 <c>BleUnlockService.Config.BleUnlockOptions</c> 的字段、默认值。
/// 服务端类型无共享引用，本文件按设计 §5.3 采用“镜像”策略；字段名与 JSON 键一致（PascalCase）。
/// </summary>
public sealed class BleUnlockConfig
{
    public DeviceConfig Device { get; set; } = new();
    public CredentialsConfig Credentials { get; set; } = new();
    public ProximityConfig Proximity { get; set; } = new();
    public NotificationConfig Notification { get; set; } = new();

    /// <summary>深拷贝（VM 快照/测试用，避免共享可变对象）。</summary>
    public BleUnlockConfig Clone() => new()
    {
        Device = new DeviceConfig
        {
            Irk = Device.Irk,
            IrkByteOrder = Device.IrkByteOrder,
            IdentityAddress = Device.IdentityAddress,
        },
        Credentials = new CredentialsConfig
        {
            UserName = Credentials.UserName,
            Password = Credentials.Password,
        },
        Proximity = new ProximityConfig
        {
            LockThresholdDbm = Proximity.LockThresholdDbm,
            UnlockThresholdDbm = Proximity.UnlockThresholdDbm,
            UnlockSampleCount = Proximity.UnlockSampleCount,
            LockPromptSampleCount = Proximity.LockPromptSampleCount,
            LockConfirmSampleCount = Proximity.LockConfirmSampleCount,
            SampleIntervalMs = Proximity.SampleIntervalMs,
            StartupGraceSeconds = Proximity.StartupGraceSeconds,
            UnlockPendingTimeoutSeconds = Proximity.UnlockPendingTimeoutSeconds,
        },
        Notification = new NotificationConfig
        {
            Mode = Notification.Mode,
            RejectSnoozeSeconds = Notification.RejectSnoozeSeconds,
            SnoozeMinutes = Notification.SnoozeMinutes,
        },
    };
}

/// <summary>IRK 字节序（镜像服务端枚举：Auto/Normal/Reversed）。</summary>
public enum IrkByteOrder
{
    Auto,
    Normal,
    Reversed,
}

/// <summary>通知方式（镜像服务端枚举：Toast/Dialog）。</summary>
public enum NotifierMode
{
    Toast,
    Dialog,
}

public sealed class DeviceConfig
{
    public string Irk { get; set; } = "";
    public IrkByteOrder IrkByteOrder { get; set; } = IrkByteOrder.Auto;
    public string? IdentityAddress { get; set; }
}

public sealed class CredentialsConfig
{
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class ProximityConfig
{
    public double LockThresholdDbm { get; set; } = -80;
    public double UnlockThresholdDbm { get; set; } = -45;

    // 批次个数（镜像服务端判定：触发读数不计入，批内每条都需达标）
    public int UnlockSampleCount { get; set; } = 3;
    public int LockPromptSampleCount { get; set; } = 5;
    public int LockConfirmSampleCount { get; set; } = 10;

    /// <summary>RSSI 采样间隔（毫秒）：0 = 每条广播都采样；上限 2000（见服务端注释）。</summary>
    public int SampleIntervalMs { get; set; } = 0;

    public double StartupGraceSeconds { get; set; } = 15;
    public double UnlockPendingTimeoutSeconds { get; set; } = 20;
}

public sealed class NotificationConfig
{
    public NotifierMode Mode { get; set; } = NotifierMode.Toast;
    public double RejectSnoozeSeconds { get; set; } = 60;
    public double SnoozeMinutes { get; set; } = 5;
}

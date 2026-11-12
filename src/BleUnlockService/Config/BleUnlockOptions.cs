namespace BleUnlockService.Config;

public sealed class BleUnlockOptions
{
    public const string SectionName = "BleUnlock";

    public DeviceOptions Device { get; set; } = new();
    public CredentialOptions Credentials { get; set; } = new();
    public ProximityOptions Proximity { get; set; } = new();
    public NotificationOptions Notification { get; set; } = new();
    public UiOptions Ui { get; set; } = new();
}

public sealed class UiOptions
{
    // system | zh | en | es; un valor desconocido equivale a system (nunca invalida la configuración)
    public string Language { get; set; } = "system";
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
    public double UnlockThresholdDbm { get; set; } = -65;

    // 判定改为批次全数达标（2026-10-18 改版）：触发读数不计入，之后每条读数都需达标；任一条不达标整批作废
    public int UnlockSampleCount { get; set; } = 3;          // 靠近确认：解锁
    public int LockPromptSampleCount { get; set; } = 5;      // 离开确认：弹窗（也是 Win+L 后的待命判定）
    public int LockConfirmSampleCount { get; set; } = 10;    // 弹窗后锁屏确认

    // RSSI 采样间隔（毫秒）：0 = 每条广播都计入；100–2000 = 定时取样，每隔这么久计入一个读数，
    // 期间没有新广播就重复上一个读数（见 SampleResampler，2026-11-04 改版）。上限 2000：
    // 解锁批次规定相邻读数间隔不得超过 3 秒。
    public int SampleIntervalMs { get; set; } = 0;

    public double StartupGraceSeconds { get; set; } = 15;
    public double UnlockPendingTimeoutSeconds { get; set; } = 20;

    // 完全收不到目标设备这么多秒，就把静默当作离开（设备关机/被拿走）。0 = 关闭这条兜底。
    // 2026-11-04 由固定 60 s 改为可配、默认 180 s：手表放着不动会停播一两分钟，人却还在
    public double NoSignalDepartureSeconds { get; set; } = 180;

    // 突变保护（2026-11-10）：触发值与之前最近 120 秒内、最多 SuddenChangeReferenceCount 个真实读数的平均值
    // 相差 SuddenChangeDeltaDb 以上 = 突变。突降（已解锁时）不弹提示，SuddenDropConfirmSeconds 秒内至少 3 个
    // 真实读数且全部低于锁定阈值就直接锁屏；突升（锁屏后）要 SuddenRiseConfirmSeconds 秒内至少 2 个真实读数
    // 全部高于解锁阈值才解锁。任一真实读数回到另一侧即取消。落差 0 = 关闭
    public int SuddenChangeReferenceCount { get; set; } = 25;
    public double SuddenChangeDeltaDb { get; set; } = 20;
    public double SuddenDropConfirmSeconds { get; set; } = 30;
    public double SuddenRiseConfirmSeconds { get; set; } = 5;
}

public sealed class NotificationOptions
{
    public NotifierMode Mode { get; set; } = NotifierMode.Toast;
    public double RejectSnoozeSeconds { get; set; } = 60;
    public double SnoozeMinutes { get; set; } = 5;
}

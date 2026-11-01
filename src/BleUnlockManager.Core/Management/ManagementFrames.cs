namespace BleUnlockManager.Core.Management;

/// <summary>管理通道帧基类（按 t 判别，设计 §3.2）。v1 字段只增不改：解析忽略未知字段。</summary>
public abstract record ManagementFrame(string T);

/// <summary>连接后的第一帧：hello（服务版本）。</summary>
public sealed record HelloFrame(string T, int V, string Svc) : ManagementFrame(T);

/// <summary>
/// 数据帧（schema v1，设计 §3.3）。引用型字段一律可空：缺字段/异常帧不抛，交由消费端显式降级（设计 §5.5）。
/// </summary>
public sealed record SnapshotFrame(
    string T,
    int V,
    long Seq,
    long Gen,
    long TsMs,
    double UptimeSec,
    SnapshotState? State,
    SnapshotScan? Scan,
    SnapshotConnection? Helper,
    SnapshotConnection? Cred,
    SnapshotThresholds? Thr,
    SnapshotAverages? Avg,
    long? LastMatchMs,
    long[][]? Samples,
    SnapshotBatch? Batch = null) : ManagementFrame(T);

/// <summary>状态机：sinceMs = 进入当前状态的服务端 UTC 毫秒（未播种时为空）。</summary>
public sealed record SnapshotState(string? Name, long? SinceMs);

/// <summary>扫描健康：status 沿用服务端原文（取值钉死见 ManagementProtocol 注释）；detail 预留。</summary>
/// <summary>Code: running | noAdapter | radioOff | startFailed (independiente del idioma). Status: texto del servicio.</summary>
public sealed record SnapshotScan(string? Status, string? Detail, string? Code);

/// <summary>管道连接态（助手/凭据）。</summary>
public sealed record SnapshotConnection(bool Connected);

/// <summary>当前生效阈值与批次参数（热重载后随之变化，曲线阈值线只用前两个）。</summary>
public sealed record SnapshotThresholds(
    double LockDbm,
    double UnlockDbm,
    int UnlockCount,
    int LockPromptCount,
    int LockConfirmCount);

/// <summary>5s/10s 窗口均值（null = 无样本）与样本数。</summary>
public sealed record SnapshotAverages(double? W5, double? W10, int N5, int N10);

/// <summary>批次进度（v1 新增字段）：kind = leave | lockConfirm | unlock；旧服务不发送时为 null。</summary>
public sealed record SnapshotBatch(string Kind, int N, int Of);

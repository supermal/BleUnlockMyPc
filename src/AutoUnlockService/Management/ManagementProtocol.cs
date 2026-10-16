using System.Text.Json;

namespace AutoUnlockService.Management;

// 管理通道 Management v1 的帧定义与序列化（设计 §3.2/§3.3）。
// 帧为单行 UTF-8 JSON、以 \n 结尾、camelCase；v1 字段只增不改，管理端忽略未知字段
public static class ManagementProtocol
{
    public const int Version = 1;
    public const string PipeName = "AutoUnlockManagement";

    // System.Text.Json 的序列化器在多线程下安全：缓存一份，避免每帧重新构建
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static HelloFrame CreateHello(string serviceVersion) =>
        new("hello", Version, serviceVersion);

    // 单行 JSON + 结尾换行（协议要求）；内容里不含换行
    public static string SerializeLine<T>(T frame) =>
        JsonSerializer.Serialize(frame, JsonOptions) + "\n";
}

// 连接后的第一帧：t=hello
public sealed record HelloFrame(string T, int V, string Svc);

// 数据帧：t=snapshot。字段名与语义锁定于设计 §3.3
public sealed record SnapshotFrame(
    string T,
    int V,
    long Seq,
    long Gen,
    long TsMs,
    double UptimeSec,
    SnapshotState State,
    SnapshotScan Scan,
    SnapshotConnection Helper,
    SnapshotConnection Cred,
    SnapshotThresholds Thr,
    SnapshotAverages Avg,
    long? LastMatchMs,
    IReadOnlyList<long[]> Samples);

// sinceMs = 进入当前状态的服务端 UTC 毫秒时间戳；服务刚启动尚未播种时为 null
public sealed record SnapshotState(string? Name, long? SinceMs);

// status 沿用现有状态上报的原文（v1 无结构化取值）。当前已知取值集合（BleScanner 的
// StatusChanged）："BLE 扫描已启动"（运行中）、"未找到支持 BLE 的蓝牙适配器"、
// "蓝牙已关闭"、"启动 BLE 扫描失败：…"（失败串为开放文本，可能带异常消息后缀；
// 另注意"重试中"目前只是日志、不经 StatusChanged 上报）。detail 预留，目前恒为 null
public sealed record SnapshotScan(string? Status, string? Detail);

public sealed record SnapshotConnection(bool Connected);

// 当前生效阈值（含热重载后的新值）
public sealed record SnapshotThresholds(
    double LockDbm,
    double UnlockDbm,
    double LockWinSec,
    double UnlockWinSec,
    int MinSamples);

// w5/w10 为 5s/10s 窗口均值（null = 无样本）；n5/n10 为窗口内样本数
public sealed record SnapshotAverages(double? W5, double? W10, int N5, int N10);

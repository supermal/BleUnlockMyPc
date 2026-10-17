using System.Globalization;

namespace AutoUnlockManager.Core;

/// <summary>扫描健康分类（概览页健康行着色用）。</summary>
public enum ScanHealth
{
    /// <summary>尚未收到扫描状态。</summary>
    Unknown,

    /// <summary>BLE 扫描运行中。</summary>
    Running,

    /// <summary>告警（适配器不可用/蓝牙关闭/启动失败等）。</summary>
    Degraded,
}

/// <summary>
/// 遥测展示格式化（概览页使用；数值一律 InvariantCulture，避免本地化逗号小数）。
/// 状态中文映射与服务端 <c>ProximityState</c> 枚举名对应（设计 §3.3）。
/// </summary>
public static class TelemetryFormat
{
    /// <summary>判黄阈值：最近匹配超过此时长未更新（设计 §5.3）。</summary>
    public static readonly TimeSpan LastMatchStaleAfter = TimeSpan.FromMinutes(5);

    /// <summary>扫描运行中的钉死取值（服务端 ManagementProtocol 注释）。</summary>
    public const string ScanRunningStatus = "BLE 扫描已启动";

    /// <summary>dBm 显示：null → 「—」；否则「-57.3 dBm」。</summary>
    public static string Dbm(double? value) =>
        value is null ? "—" : value.Value.ToString("0.0", CultureInfo.InvariantCulture) + " dBm";

    /// <summary>状态枚举名 → 中文；未知取值原文呈现（v2 前向兼容）。</summary>
    public static string StateName(string? name) => name switch
    {
        "Unlocked" => "已解锁",
        "PendingLock" => "待锁定",
        "Snoozed" => "已暂缓",
        "LockedWaitingDeparture" => "已锁定·等待离开",
        "LockedArmed" => "已锁定·监控中",
        "UnlockPending" => "解锁确认中",
        "Suspended" => "已挂起",
        null or "" => "未知",
        _ => name,
    };

    /// <summary>「已持续」时长（sinceMs/noWMs 均为服务端 UTC 毫秒；sinceMs 缺失 → 「—」）。</summary>
    public static string Since(long? sinceMs, long nowMs)
    {
        if (sinceMs is null)
        {
            return "—";
        }

        return Duration(TimeSpan.FromMilliseconds(Math.Max(0, nowMs - sinceMs.Value)));
    }

    /// <summary>时长文案：「45 秒」/「3 分 12 秒」/「1 小时 5 分」。</summary>
    public static string Duration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours} 小时 {duration.Minutes} 分";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{(int)duration.TotalMinutes} 分 {duration.Seconds} 秒";
        }

        return $"{(int)duration.TotalSeconds} 秒";
    }

    /// <summary>服务运行时长（uptimeSec → 同时长口径）。</summary>
    public static string Uptime(double uptimeSec) =>
        Duration(TimeSpan.FromSeconds(Math.Max(0, uptimeSec)));

    /// <summary>最近匹配：「42 秒前」/「8 分钟前」；null → 「从未」（本次运行）。</summary>
    public static string LastMatch(long? lastMatchMs)
    {
        if (lastMatchMs is null)
        {
            return "从未";
        }

        var seconds = Math.Max(0, lastMatchMs.Value) / 1000.0;
        if (seconds < 60)
        {
            return $"{(long)seconds} 秒前"; // 向下取整：59.6s → 「59 秒前」
        }

        if (seconds < 3600)
        {
            return $"{(long)(seconds / 60)} 分钟前";
        }

        return $"{(long)(seconds / 3600)} 小时前";
    }

    /// <summary>最近匹配是否过期（标黄）：匹配距今超过 5 分钟；从未匹配时按运行时长判断。</summary>
    public static bool IsLastMatchStale(long? lastMatchMs, double uptimeSec) =>
        lastMatchMs is null
            ? uptimeSec > LastMatchStaleAfter.TotalSeconds
            : lastMatchMs.Value > LastMatchStaleAfter.TotalMilliseconds;

    /// <summary>扫描状态分类（取值钉死："BLE 扫描已启动"=运行中；其余非空=告警；null=未知）。</summary>
    public static ScanHealth ClassifyScan(string? status) => status switch
    {
        null or "" => ScanHealth.Unknown,
        ScanRunningStatus => ScanHealth.Running,
        _ => ScanHealth.Degraded,
    };
}

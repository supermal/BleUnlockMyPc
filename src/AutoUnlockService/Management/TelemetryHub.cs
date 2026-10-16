using AutoUnlockService.Config;
using AutoUnlockService.Core;

namespace AutoUnlockService.Management;

// 遥测中枢：旁路订阅 BLE 样本、状态机变更、扫描状态与助手/凭据连接态，
// 维护环形缓冲并在管理端在线时组装快照。本类不触及服务主流程：
// 摄入方法一律不向外抛异常（它们从 BLE 回调等主流程线程被调用）
public sealed class TelemetryHub
{
    public const int SampleCapacity = 4096;

    // 环形缓冲的样本保留窗口：连接后首帧回溯最近 5 分钟（先到者为准）
    public static readonly TimeSpan SampleRetention = TimeSpan.FromSeconds(300);

    // 与 RssiHistory 相同思路：条数超过阈值才做按时间裁剪，避免每条样本全量扫描
    private const int PruneThreshold = 16;

    private readonly OptionsGate _options;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly DateTimeOffset _startedAt;
    private readonly object _lock = new();
    private readonly SampleEntry[] _ring = new SampleEntry[SampleCapacity];

    // 5s/10s 窗口均值复用现有 RssiHistory；保留 1 分钟足够覆盖 10s 窗口
    private readonly RssiHistory _windows;

    private int _ringStart;             // 最旧样本所在槽位
    private int _ringCount;
    private long _nextSampleIndex;      // 已摄入样本总数（下一个样本的全局序号）
    private long _sessionCursor;        // 当前会话已发送到的全局序号（不含）
    private bool _firstSnapshotPending = true;
    private long _seq;
    private long _gen = 1;              // 历史代次：每次 OnHistoryReset 递增，管理端据此清空曲线
    private string? _stateName;
    private long? _stateSinceMs;
    private string? _scanStatus;
    private bool _helperConnected;
    private bool _credConnected;
    private DateTimeOffset? _lastMatchAt;

    public TelemetryHub(
        OptionsGate options,
        TimeProvider time,
        ILogger<TelemetryHub> log)
    {
        _options = options;
        _time = time;
        _log = log;
        // uptimeSec 自 hub 构造起算：与进程启动只差一次性的构造偏移
        _startedAt = time.GetUtcNow();
        _windows = new RssiHistory(TimeSpan.FromMinutes(1), time);
    }

    // 状态/扫描/连接发生变化时触发（立即补推用）；样本不触发，只按节拍随帧携带。
    // 约定：仅管理管道的单个会话订阅，订阅/退订发生在会话自己的线程上；触发方来自任意摄入线程
    public event Action? Changed;

    // 摄入一条匹配到目标设备的样本；tsMs 为样本的 UTC 毫秒时间戳
    public void OnSample(long tsMs, short rssi)
    {
        try
        {
            var now = _time.GetUtcNow();
            lock (_lock)
            {
                AppendSampleLocked(tsMs, rssi, now);
                _lastMatchAt = now;
            }

            // 窗口均值的口径与 Worker 的 RssiHistory 一致：同一批样本、同一个时钟
            _windows.Add(rssi, DateTimeOffset.FromUnixTimeMilliseconds(tsMs));
        }
        catch (Exception ex)
        {
            _log.LogWarning("记录 RSSI 样本时出错（已忽略）：{Message}", ex.Message);
        }
    }

    // 状态机进入新状态；同名重复调用是幂等的（保留首次进入的时间）
    public void OnStateChange(ProximityState state)
    {
        try
        {
            var name = state.ToString();
            bool raised;
            lock (_lock)
            {
                raised = _stateName != name;
                if (raised)
                {
                    _stateName = name;
                    _stateSinceMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
                }
            }

            if (raised) RaiseChanged();
        }
        catch (Exception ex)
        {
            _log.LogWarning("记录状态变更时出错（已忽略）：{Message}", ex.Message);
        }
    }

    // 扫描状态原文上报（v1 沿用现有文本语义）
    public void OnScanStatus(string status)
    {
        try
        {
            bool raised;
            lock (_lock)
            {
                raised = _scanStatus != status;
                _scanStatus = status;
            }

            if (raised) RaiseChanged();
        }
        catch (Exception ex)
        {
            _log.LogWarning("记录扫描状态时出错（已忽略）：{Message}", ex.Message);
        }
    }

    public void OnHelperConnection(bool connected)
    {
        try
        {
            bool raised;
            lock (_lock)
            {
                raised = _helperConnected != connected;
                _helperConnected = connected;
            }

            if (raised) RaiseChanged();
        }
        catch (Exception ex)
        {
            _log.LogWarning("记录助手连接状态时出错（已忽略）：{Message}", ex.Message);
        }
    }

    public void OnCredConnection(bool connected)
    {
        try
        {
            bool raised;
            lock (_lock)
            {
                raised = _credConnected != connected;
                _credConnected = connected;
            }

            if (raised) RaiseChanged();
        }
        catch (Exception ex)
        {
            _log.LogWarning("记录凭据提供程序连接状态时出错（已忽略）：{Message}", ex.Message);
        }
    }

    // 新客户端连接：下一帧 snapshot 携带最近 5 分钟回溯
    public void BeginClientSession()
    {
        lock (_lock) { _firstSnapshotPending = true; }
    }

    // 服务侧历史已作废（休眠恢复/设备变更）：清空环形缓冲与窗口均值，gen 递增。
    // 样本序号游标保持单调不回退：重置后的回溯为空、增量自然衔接
    public void OnHistoryReset()
    {
        try
        {
            lock (_lock)
            {
                Array.Clear(_ring);
                _ringStart = 0;
                _ringCount = 0;
                _gen++;
            }

            // Clear（而非 Reset）：设备仍然存在，只是历史不再代表当前（与 Worker 休眠口径一致）
            _windows.Clear();
            RaiseChanged();
        }
        catch (Exception ex)
        {
            _log.LogWarning("重置遥测历史时出错（已忽略）：{Message}", ex.Message);
        }
    }

    // 组装并序列化一帧快照；仅在管理端在线时调用（无客户端时零序列化开销）
    public string BuildSnapshot()
    {
        SnapshotFrame frame;
        var now = _time.GetUtcNow();

        lock (_lock)
        {
            var cutoffMs = now.ToUnixTimeMilliseconds() - (long)SampleRetention.TotalMilliseconds;
            var samples = CollectSamplesLocked(cutoffMs);

            var p = _options.Current.Proximity;
            // 均值口径：RssiHistory.InWindow 会丢弃未来时间戳的样本；
            // 环形缓冲（samples）只在输出侧过滤过旧样本、保留未来标记（有意差异，见设计说明）
            var w5 = _windows.InWindow(now, TimeSpan.FromSeconds(5));
            var w10 = _windows.InWindow(now, TimeSpan.FromSeconds(10));

            frame = new SnapshotFrame(
                "snapshot",
                ManagementProtocol.Version,
                ++_seq,
                _gen,
                now.ToUnixTimeMilliseconds(),
                (now - _startedAt).TotalSeconds,
                new SnapshotState(_stateName, _stateSinceMs),
                new SnapshotScan(_scanStatus, null),
                new SnapshotConnection(_helperConnected),
                new SnapshotConnection(_credConnected),
                new SnapshotThresholds(
                    p.LockThresholdDbm,
                    p.UnlockThresholdDbm,
                    p.LockWindowSeconds,
                    p.UnlockWindowSeconds,
                    p.MinSamples),
                new SnapshotAverages(
                    w5.Count == 0 ? null : w5.Average,
                    w10.Count == 0 ? null : w10.Average,
                    w5.Count,
                    w10.Count),
                _lastMatchAt is { } matchAt
                    ? Math.Max(0, (long)(now - matchAt).TotalMilliseconds)
                    : null,
                samples);
        }

        return ManagementProtocol.SerializeLine(frame);
    }

    private void AppendSampleLocked(long tsMs, short rssi, DateTimeOffset now)
    {
        int slot;
        if (_ringCount == SampleCapacity)
        {
            // 缓冲已满：覆盖最旧的一条（上限优先于保留窗口）
            slot = _ringStart;
            _ringStart = (_ringStart + 1) % SampleCapacity;
        }
        else
        {
            slot = (_ringStart + _ringCount) % SampleCapacity;
            _ringCount++;
        }

        _ring[slot] = new SampleEntry(tsMs, rssi);
        _nextSampleIndex++;

        if (_ringCount > PruneThreshold)
        {
            var cutoffMs = now.ToUnixTimeMilliseconds() - (long)SampleRetention.TotalMilliseconds;
            while (_ringCount > 0 && _ring[_ringStart].TsMs < cutoffMs)
            {
                _ringStart = (_ringStart + 1) % SampleCapacity;
                _ringCount--;
            }
        }
    }

    private List<long[]> CollectSamplesLocked(long cutoffMs)
    {
        long firstBufferedIndex = _nextSampleIndex - _ringCount;

        // 首帧 = 最近 5 分钟回溯；后续 = 自上次推送的增量（按全局序号裁剪）
        long fromIndex = _firstSnapshotPending
            ? firstBufferedIndex
            : Math.Max(_sessionCursor, firstBufferedIndex);

        var samples = new List<long[]>((int)Math.Min(_nextSampleIndex - fromIndex, _ringCount));
        for (long i = fromIndex; i < _nextSampleIndex; i++)
        {
            var entry = _ring[(int)((_ringStart + (i - firstBufferedIndex)) % SampleCapacity)];

            // 输出时同样按保留窗口过滤：时钟回拨/迟到补投的旧样本不进曲线。
            // 未来时间戳（时钟回拨）不做上界过滤：样本原样保留，均值侧另有口径。
            // 乱序样本可能滞留在中间位置直到从头部滚出，这一步保证语义正确
            if (entry.TsMs < cutoffMs) continue;

            samples.Add([entry.TsMs, entry.Rssi]);
        }

        _sessionCursor = _nextSampleIndex;
        _firstSnapshotPending = false;
        return samples;
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex)
        {
            // 订阅者（管理管道）出错不能影响摄入方，更不能冒泡到服务主流程
            _log.LogWarning("遥测变化订阅者抛出异常（已忽略）：{Message}", ex.Message);
        }
    }

    private readonly struct SampleEntry(long tsMs, short rssi)
    {
        public long TsMs { get; } = tsMs;
        public short Rssi { get; } = rssi;
    }
}

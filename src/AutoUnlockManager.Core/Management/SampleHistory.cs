namespace AutoUnlockManager.Core.Management;

/// <summary>样本点：tsMs = 服务端 UTC 毫秒时间戳；Rssi = dBm。</summary>
public readonly record struct SamplePoint(long TsMs, double Rssi);

/// <summary>
/// 客户端 5 分钟滚动样本缓冲（设计 §5.3）：消费端固定在 UI 线程使用（无锁）。
/// 容忍坏行（内层数组不足/缺失一律跳过）；追加后按时间排序；按服务端时间裁剪；
/// 降采样保峰谷（min/max 分桶）且 ≤maxPoints。
/// </summary>
public sealed class SampleHistory
{
    /// <summary>默认保留窗口：5 分钟（与曲线窗口一致）。</summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromMinutes(5);

    /// <summary>条数上限（服务端时钟异常/回拨的防御；正常 5 分钟 @4/s ≈ 1200 条，服务端环缓冲 4096）。</summary>
    public const int MaxCapacity = 8192;

    private readonly List<SamplePoint> _points = [];

    public SampleHistory(TimeSpan? retention = null)
        => Retention = retention ?? DefaultRetention;

    /// <summary>保留窗口。</summary>
    public TimeSpan Retention { get; }

    /// <summary>当前样本数。</summary>
    public int Count => _points.Count;

    /// <summary>最近一条样本（无样本 → null）。</summary>
    public SamplePoint? Latest => _points.Count > 0 ? _points[^1] : null;

    public void Clear() => _points.Clear();

    /// <summary>追加样本行（每行 [tsMs, rssi]；null/长度不足行忽略；追加后按时间稳定排序）。</summary>
    public void AddRange(long[][]? rows)
    {
        if (rows is null || rows.Length == 0)
        {
            return;
        }

        var added = false;
        foreach (var row in rows)
        {
            if (row is null || row.Length < 2)
            {
                continue; // 坏行：忽略
            }

            _points.Add(new SamplePoint(row[0], row[1]));
            added = true;
        }

        if (added)
        {
            // 服务端通常有序；迟到/乱序样本可能混入，统一按时间稳定排序（OrderBy 稳定：同刻样本保持到达顺序）
            var ordered = _points.OrderBy(static p => p.TsMs).ToList();
            _points.Clear();
            _points.AddRange(ordered);

            // 容量上限：时钟异常（时间回拨致裁剪失效）时防无界增长，丢最旧
            if (_points.Count > MaxCapacity)
            {
                _points.RemoveRange(0, _points.Count - MaxCapacity);
            }
        }
    }

    /// <summary>裁剪早于「nowMs - Retention」的旧样本（nowMs = 服务端当前时间）。</summary>
    public void Prune(long nowMs)
    {
        var cutoffMs = nowMs - (long)Retention.TotalMilliseconds;
        var drop = 0;
        while (drop < _points.Count && _points[drop].TsMs < cutoffMs)
        {
            drop++;
        }

        if (drop > 0)
        {
            _points.RemoveRange(0, drop);
        }
    }

    /// <summary>样本快照（副本；供渲染/导出）。</summary>
    public IReadOnlyList<SamplePoint> Snapshot() => _points.ToArray();

    /// <summary>
    /// 降采样（设计 §5.3：≤600 点）：按 min/max 分桶（保峰谷、保持时间单调），
    /// 桶序即时间序；样本数不超过上限时原样返回。
    /// </summary>
    public IReadOnlyList<SamplePoint> Downsample(int maxPoints = 600)
    {
        if (maxPoints < 4 || _points.Count <= maxPoints)
        {
            return Snapshot();
        }

        var buckets = maxPoints / 2; // 每桶最多 min+max 两点
        var result = new List<SamplePoint>(maxPoints + 2);
        for (var bucket = 0; bucket < buckets; bucket++)
        {
            var start = (int)((long)bucket * _points.Count / buckets);
            var end = (int)((long)(bucket + 1) * _points.Count / buckets);
            if (end <= start)
            {
                continue;
            }

            var minIndex = start;
            var maxIndex = start;
            for (var i = start + 1; i < end; i++)
            {
                if (_points[i].Rssi < _points[minIndex].Rssi)
                {
                    minIndex = i;
                }
                if (_points[i].Rssi > _points[maxIndex].Rssi)
                {
                    maxIndex = i;
                }
            }

            if (minIndex == maxIndex)
            {
                result.Add(_points[minIndex]);
            }
            else if (minIndex < maxIndex)
            {
                result.Add(_points[minIndex]);
                result.Add(_points[maxIndex]);
            }
            else
            {
                result.Add(_points[maxIndex]);
                result.Add(_points[minIndex]);
            }
        }

        return result;
    }
}

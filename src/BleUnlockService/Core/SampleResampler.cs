namespace BleUnlockService.Core;

/// <summary>
/// 定时取样（SampleIntervalMs &gt; 0 时生效，2026-11-04 用户要求）：每隔 SampleIntervalMs 取一个读数计入判定。
/// 这段时间里收到过广播，就取最新的一条；一条都没收到，就重复上一个读数。
/// 0 = 不定时取样，每条广播直接计入（原做法，不经过本类）。
/// 重复读数会让一个读数被当成多个使用（锁屏与解锁都受影响），这是用户确认过的取舍。
/// 例外：蓝牙接收卡死时不重复（没有新数据，重复只会制造假证据）；连续 holdLimit 没有任何真实读数后
/// 也不再重复，好让「无信号判离开」兜底仍能生效（设备关机或被带走）。
/// </summary>
internal sealed class SampleResampler(TimeProvider time)
{
    private readonly object _lock = new();
    private short? _pending;        // 上一次取样以来收到的最新读数
    private short? _last;           // 最近一条真实读数（重复用）
    private DateTimeOffset _lastRealAt;

    /// <summary>收到一条目标设备的真实读数（哨兵值已在上游过滤）。</summary>
    public void Offer(short rssi)
    {
        lock (_lock)
        {
            _pending = rssi;
            _last = rssi;
            _lastRealAt = time.GetUtcNow();
        }
    }

    /// <summary>
    /// 到了取样时刻：返回要计入的读数，null = 这次不计入。
    /// holdAllowed = false 时只取真实读数；holdLimit 为零表示重复不设时限。
    /// </summary>
    public short? Take(bool holdAllowed, TimeSpan holdLimit)
    {
        lock (_lock)
        {
            if (_pending is { } fresh)
            {
                _pending = null;
                return fresh;
            }

            if (!holdAllowed || _last is not { } last) return null;
            if (holdLimit > TimeSpan.Zero && time.GetUtcNow() - _lastRealAt >= holdLimit) return null;
            return last;
        }
    }

    /// <summary>设备切换、睡眠唤醒后：旧读数不再代表任何东西，不能拿来重复。</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _pending = null;
            _last = null;
        }
    }
}

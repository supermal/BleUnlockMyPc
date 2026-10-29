namespace AutoUnlockService.Core;

/// <summary>
/// RSSI 采样限流：同一设备两次计入判定的读数之间至少间隔 <c>SampleIntervalMs</c>。
/// 0 表示不限制（每条广播都计入）。使用 <see cref="TimeProvider"/> 的时间戳，
/// 不受系统时钟调整影响；设备切换或恢复睡眠时调用 <see cref="Reset"/>。
/// </summary>
internal sealed class SampleIntervalGate(TimeProvider time)
{
    private readonly object _lock = new();
    private long _lastAccepted;
    private bool _hasLast;

    public bool ShouldAccept(int intervalMs)
    {
        if (intervalMs <= 0)
        {
            return true;
        }

        lock (_lock)
        {
            var now = time.GetTimestamp();
            if (_hasLast &&
                time.GetElapsedTime(_lastAccepted, now) < TimeSpan.FromMilliseconds(intervalMs))
            {
                return false;
            }

            _lastAccepted = now;
            _hasLast = true;
            return true;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _hasLast = false;
        }
    }
}

namespace AutoUnlockManager.Services;

/// <summary>
/// 单实例守卫（设计 §5.2）：Global 互斥体 + 命名唤醒事件。
/// 首实例持有互斥体，并在后台线程监听唤醒事件；后续实例置位唤醒事件（按已有实例处理）后退出。
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    // 互斥体/事件名与设计 §5.2 锁定；Global 前缀保证跨会话可见
    private const string MutexName = @"Global\AutoUnlockManager.SingleInstance.v1";
    private const string WakeupEventName = @"Global\AutoUnlockManager.Wakeup.v1";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _wakeupEvent;
    private readonly Action _onWakeupRequested;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread? _waitThread;

    private SingleInstanceGuard(Mutex mutex, EventWaitHandle? wakeupEvent, Action onWakeupRequested)
    {
        _mutex = mutex;
        _wakeupEvent = wakeupEvent;
        _onWakeupRequested = onWakeupRequested;

        if (wakeupEvent is not null)
        {
            _waitThread = new Thread(WaitLoop)
            {
                IsBackground = true,
                Name = "单实例唤醒监听",
            };
            _waitThread.Start();
        }
    }

    /// <summary>
    /// 尝试成为首实例。已有实例（或打开互斥体被拒/其他异常，按已有实例处理）时：
    /// 尽力置位唤醒事件请求唤出主窗口，然后返回 null 供调用方退出。
    /// </summary>
    public static SingleInstanceGuard? TryCreate(Action onWakeupRequested)
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (!createdNew)
            {
                // 已有实例：请求其唤出主窗口，随后退出
                NotifyExistingInstance();
                mutex.Dispose();
                return null;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // 打开互斥体被拒（ACL）：按已有实例处理
            NotifyExistingInstance();
            return null;
        }
        catch
        {
            // 无法判定单实例语义时按已有实例处理，避免双开
            return null;
        }

        // 首实例：创建唤醒事件；失败则退化为“无唤醒能力”，但单实例语义仍成立
        EventWaitHandle? wakeupEvent = null;
        try
        {
            wakeupEvent = new EventWaitHandle(
                initialState: false, EventResetMode.AutoReset, WakeupEventName, out _);
        }
        catch
        {
            // 事件创建失败：主窗口仍可用，仅二实例无法唤出
        }

        return new SingleInstanceGuard(mutex, wakeupEvent, onWakeupRequested);
    }

    /// <summary>
    /// 置位唤醒事件。事件可能尚未创建（首实例启动竞态）：短暂重试后放弃（仅影响唤醒，不影响单实例语义）。
    /// </summary>
    private static void NotifyExistingInstance()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var wakeupEvent = EventWaitHandle.OpenExisting(WakeupEventName);
                wakeupEvent.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // 事件不存在：首实例可能仍在启动流程中，等一小段再试
                Thread.Sleep(200);
            }
            catch
            {
                // 打开被拒或其他异常：无法唤醒，按已有实例直接退出
                return;
            }
        }
    }

    /// <summary>后台等待循环：收到唤醒信号后回调（调用方负责切换到 UI 线程）。</summary>
    private void WaitLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                // 1s 超时轮询，便于退出时尽快观察到取消
                if (_wakeupEvent!.WaitOne(TimeSpan.FromSeconds(1)))
                {
                    try
                    {
                        _onWakeupRequested();
                    }
                    catch
                    {
                        // 单个唤醒回调异常不得终止监听
                    }
                }
            }
            catch
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
        }
        catch
        {
            // 忽略
        }
        try
        {
            _wakeupEvent?.Set(); // 尽快唤醒等待线程
        }
        catch
        {
            // 忽略
        }
        try
        {
            _waitThread?.Join(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // 忽略
        }
        try
        {
            _wakeupEvent?.Dispose();
        }
        catch
        {
            // 忽略
        }
        try
        {
            _mutex.ReleaseMutex(); // 非持有线程调用时抛异常，忽略
        }
        catch
        {
            // 忽略
        }
        try
        {
            _mutex.Dispose();
        }
        catch
        {
            // 忽略
        }
        try
        {
            _cts.Dispose();
        }
        catch
        {
            // 忽略
        }
    }
}

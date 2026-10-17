using System.IO.Pipes;
using System.Text;

namespace AutoUnlockManager.Core.Management;

/// <summary>
/// 管理通道管道客户端（设计 §3.1/§5.5）：push-only 读方。
/// 连接失败/断开 → 固定退避重连；坏帧忽略不中断；事件在后台线程触发（UI 端自行调度）；
/// <see cref="Dispose"/> 取消并释放当前连接，干净结束循环。
/// </summary>
public sealed class ManagementClient : IDisposable
{
    /// <summary>重连退避（设计 §5.5：2 秒）。</summary>
    public static readonly TimeSpan DefaultReconnectDelay = TimeSpan.FromSeconds(2);

    /// <summary>单次连接等待上限：服务端单客户端占用清理窗口（约 5s）内多次短等，避免长挂。</summary>
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(3);

    private readonly string _pipeName;
    private readonly TimeSpan _reconnectDelay;
    private readonly TimeSpan _connectTimeout;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private NamedPipeClientStream? _currentPipe; // Volatile 访问（跨线程置位/读取）
    private int _connected; // 0/1，Interlocked
    private bool _disposed;

    /// <summary>收到可解析帧（hello/snapshot）。后台线程；订阅者异常被隔离。</summary>
    public event EventHandler<ManagementFrame>? FrameReceived;

    /// <summary>连接态变化（true=已建立、false=断开/失败）。仅状态变化时触发。后台线程；订阅者异常被隔离。</summary>
    public event EventHandler<bool>? ConnectionChanged;

    public ManagementClient(
        string? pipeName = null,
        TimeSpan? reconnectDelay = null,
        TimeSpan? connectTimeout = null)
    {
        _pipeName = pipeName ?? ServiceInfo.ManagementPipeName;
        _reconnectDelay = reconnectDelay ?? DefaultReconnectDelay;
        _connectTimeout = connectTimeout ?? DefaultConnectTimeout;
        _loop = Task.Run(RunAsync);
    }

    /// <summary>当前是否持有活动连接。</summary>
    public bool IsConnected => Volatile.Read(ref _connected) == 1;

    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await ConnectAndReadAsync(_cts.Token).ConfigureAwait(false);
            }
            catch
            {
                // 连接/读取异常 → 统一走退避重试
            }

            SetConnected(false);

            if (_cts.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(_reconnectDelay, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ConnectAndReadAsync(CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.In, PipeOptions.Asynchronous);
        Volatile.Write(ref _currentPipe, pipe);

        try
        {
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeoutCts.CancelAfter(_connectTimeout);
                try
                {
                    await pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // 连接超时（服务端可能仍被旧客户端占用）：本轮放弃，交给外层退避
                    return;
                }
            }

            SetConnected(true);

            using var reader = new StreamReader(pipe, Encoding.UTF8);
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (line is null)
                {
                    break; // 服务端关闭（服务停止/重启/被顶替清理）
                }

                var frame = ManagementFrameParser.Parse(line);
                if (frame is not null)
                {
                    RaiseFrame(frame);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _currentPipe, null);
        }
    }

    private void SetConnected(bool connected)
    {
        var value = connected ? 1 : 0;
        if (Interlocked.Exchange(ref _connected, value) == value)
        {
            return; // 无变化
        }

        RaisePerHandler(ConnectionChanged, connected);
    }

    private void RaiseFrame(ManagementFrame frame) => RaisePerHandler(FrameReceived, frame);

    /// <summary>逐订阅者隔离调用：单个订阅者异常不影响其他订阅者与连接循环。</summary>
    private void RaisePerHandler<T>(EventHandler<T>? handlers, T payload)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<T>)handler)(this, payload);
            }
            catch
            {
                // 忽略：订阅者异常不得终止连接循环
            }
        }
    }

    /// <summary>停止循环并释放当前连接（阻塞至后台循环退出或最多 2 秒）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        try
        {
            _cts.Cancel();
        }
        catch
        {
            // 忽略
        }

        // 释放管道以唤醒挂起的 Read/Connect（取消令牌无法中断所有管道等待）
        try
        {
            Volatile.Read(ref _currentPipe)?.Dispose();
        }
        catch
        {
            // 忽略
        }

        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
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

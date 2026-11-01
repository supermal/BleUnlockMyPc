using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using BleUnlockManager.Core.Management;
using Xunit;

namespace BleUnlockManager.Tests;

/// <summary>
/// ManagementClient 真管道集成测试（本机命名管道，随机管道名互不干扰）。
/// 退避注入 50ms 保证重连断言在秒级内完成。
/// </summary>
public sealed class ManagementClientTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromMilliseconds(50);

    private const string HelloLine = """{"t":"hello","v":1,"svc":"test"}""";
    private const string SnapshotLine =
        """{"t":"snapshot","v":1,"seq":813,"gen":1,"tsMs":1760648000123,"uptimeSec":10,"samples":[[1000,-60]]}""";

    [Fact]
    public async Task ReceivesFrames_AndIgnoresGarbage()
    {
        var pipeName = NewPipeName();
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var connectedStates = new List<bool>();
        var snapshots = new List<SnapshotFrame>();
        var hellos = new List<HelloFrame>();

        using var client = new ManagementClient(pipeName, ReconnectDelay);
        client.ConnectionChanged += (_, connected) => { lock (connectedStates) { connectedStates.Add(connected); } };
        client.FrameReceived += (_, frame) =>
        {
            if (frame is SnapshotFrame s)
            {
                lock (snapshots) { snapshots.Add(s); }
            }
            else if (frame is HelloFrame h)
            {
                lock (hellos) { hellos.Add(h); }
            }
        };

        var accept = server.WaitForConnectionAsync();
        using var writer = new StreamWriter(server, Encoding.UTF8);
        await accept.WaitAsync(TestTimeout);

        await writer.WriteLineAsync(HelloLine);
        await writer.WriteLineAsync("garbage not json");   // 坏行：忽略不中断
        await writer.WriteLineAsync(SnapshotLine);
        await writer.FlushAsync();

        await WaitUntilAsync(() => { lock (snapshots) { return snapshots.Count >= 1; } });

        SnapshotFrame snapshot;
        lock (snapshots) { snapshot = snapshots[0]; }
        Assert.Equal(813, snapshot.Seq);
        Assert.True(client.IsConnected);
        lock (connectedStates) { Assert.Contains(true, connectedStates); }
        lock (hellos) { Assert.Single(hellos); Assert.Equal("test", hellos[0].Svc); }
    }

    [Fact]
    public async Task ReconnectsAfterServerDrop()
    {
        var pipeName = NewPipeName();
        var connectedStates = new List<bool>();
        var snapshotSeqs = new List<long>();

        using var client = new ManagementClient(pipeName, ReconnectDelay);
        client.ConnectionChanged += (_, connected) => { lock (connectedStates) { connectedStates.Add(connected); } };
        client.FrameReceived += (_, frame) => { if (frame is SnapshotFrame s) { lock (snapshotSeqs) { snapshotSeqs.Add(s.Seq); } } };

        // 第一个会话：发一帧后关闭（模拟服务停止）
        {
            await using var server1 = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var accept1 = server1.WaitForConnectionAsync();
            await accept1.WaitAsync(TestTimeout);
            using var writer1 = new StreamWriter(server1, Encoding.UTF8);
            await writer1.WriteLineAsync(SnapshotLine);
            await writer1.FlushAsync();
            await WaitUntilAsync(() => { lock (snapshotSeqs) { return snapshotSeqs.Count >= 1; } });
        }

        // 服务端关闭 → 客户端应观察到断开
        await WaitUntilAsync(() => { lock (connectedStates) { return connectedStates.Contains(false); } });

        // 第二个会话：重连后应再次收到帧（seq 999）
        await using var server2 = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accept2 = server2.WaitForConnectionAsync();
        using var writer2 = new StreamWriter(server2, Encoding.UTF8);
        await accept2.WaitAsync(TestTimeout);
        await writer2.WriteLineAsync(SnapshotLine.Replace("\"seq\":813", "\"seq\":999"));
        await writer2.FlushAsync();

        await WaitUntilAsync(() =>
        {
            lock (snapshotSeqs) { return snapshotSeqs.Contains(999); }
        });

        Assert.True(client.IsConnected);
    }

    [Fact]
    public void Dispose_StopsCleanly()
    {
        var pipeName = NewPipeName();
        var client = new ManagementClient(pipeName, ReconnectDelay);

        client.Dispose();
        client.Dispose(); // 幂等

        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Dispose_WithPendingRead_ReturnsQuickly()
    {
        var pipeName = NewPipeName();
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var received = 0;

        var client = new ManagementClient(pipeName, ReconnectDelay);
        client.FrameReceived += (_, _) => Interlocked.Increment(ref received);

        var accept = server.WaitForConnectionAsync();
        using var writer = new StreamWriter(server, Encoding.UTF8);
        await accept.WaitAsync(TestTimeout);
        await writer.WriteLineAsync(HelloLine);
        await writer.FlushAsync();
        await WaitUntilAsync(() => Volatile.Read(ref received) >= 1); // 已进入挂起读

        var stopwatch = Stopwatch.StartNew();
        client.Dispose(); // 必须释放管道以唤醒挂起读（不能依赖取消令牌）
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Dispose 用时 {stopwatch.Elapsed}");
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task SubscriberException_DoesNotKillLoop()
    {
        var pipeName = NewPipeName();
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var delivered = 0;

        using var client = new ManagementClient(pipeName, ReconnectDelay);
        client.FrameReceived += (_, _) => throw new InvalidOperationException("订阅者故障");
        client.FrameReceived += (_, _) => Interlocked.Increment(ref delivered);

        var accept = server.WaitForConnectionAsync();
        using var writer = new StreamWriter(server, Encoding.UTF8);
        await accept.WaitAsync(TestTimeout);
        await writer.WriteLineAsync(SnapshotLine);
        await writer.WriteLineAsync(SnapshotLine);
        await writer.FlushAsync();

        // 首个订阅者抛异常：第二个订阅者仍应收到两帧（逐订阅者隔离）
        await WaitUntilAsync(() => Volatile.Read(ref delivered) >= 2);
    }

    private static string NewPipeName() => $"BleUnlockManagerTest-{Guid.NewGuid():N}";

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TestTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(25);
        }

        Assert.Fail("等待条件超时");
    }
}

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using BleUnlockService.Resources;

namespace BleUnlockService.Management;

// \\.\pipe\BleUnlockManagement：只读遥测通道。服务端只写不读、单活动客户端；
// 客户端断开或写入失败即清理会话，回到等待（旧连接不占用、新连接即可接入）。
// 任何故障只记录日志，绝不冒泡到服务主流程
public sealed class ManagementPipeServer : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RebuildDelay = TimeSpan.FromSeconds(2);

    private readonly TelemetryHub _hub;
    private readonly ILogger<ManagementPipeServer> _log;
    private readonly TimeProvider _time;
    private readonly string _pipeName;
    private readonly PipeSecurity _security;

    // 状态/连接变化的唤醒信号：让补推不必等下一个节拍（样本不触发，避免高频推送）
    private TaskCompletionSource<bool> _changedSignal = NewSignal();

    public ManagementPipeServer(
        TelemetryHub hub,
        ILogger<ManagementPipeServer> log,
        TimeProvider? time = null,
        string pipeName = ManagementProtocol.PipeName)
        : this(hub, log, time ?? TimeProvider.System, pipeName, CreateDefaultSecurity())
    {
    }

    // 仅供测试替换 DACL（生产路径恒为 SYSTEM + Administrators 完全控制）
    internal ManagementPipeServer(
        TelemetryHub hub,
        ILogger<ManagementPipeServer> log,
        TimeProvider time,
        string pipeName,
        PipeSecurity security)
    {
        _hub = hub;
        _log = log;
        _time = time;
        _pipeName = pipeName;
        _security = security;
    }

    private static string ServiceVersion =>
        typeof(ManagementPipeServer).Assembly.GetName().Version?.ToString() ?? "unknown";

    // 接受循环：同一时间只服务一个客户端。没有主动"顶替"动作——旧连接在下一次
    // 写入失败（≤1 个写周期）或断开后被清理，实例释放，新客户端即可接入；
    // 旧连接仍在线时新客户端会等待（ConnectAsync 阻塞）而不是被拒绝
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(ct);
                await RunSessionAsync(pipe, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 建立/等待连接失败不能杀死循环；重建前稍作退避
                _log.LogWarning(ServiceStrings.Log_MgmtPipeError, ex.Message);
                try { await Task.Delay(RebuildDelay, _time, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task RunSessionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        _log.LogInformation(ServiceStrings.Log_ManagerConnected);
        _hub.BeginClientSession();
        _hub.Changed += PulseChanged;
        try
        {
            await WriteLineAsync(
                pipe, ManagementProtocol.SerializeLine(ManagementProtocol.CreateHello(ServiceVersion)), ct);

            while (!ct.IsCancellationRequested)
            {
                // 首帧含最近 5 分钟回溯；之后为增量。仅在客户端在线时才组装/序列化
                await WriteLineAsync(pipe, _hub.BuildSnapshot(), ct);

                // 常规节拍；状态/连接变化时被唤醒立即补推
                await WaitForTickOrChangeAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 停服取消是正常路径；只有写超时触发（stop 未请求）才需要留痕
            if (!ct.IsCancellationRequested)
                _log.LogWarning(ServiceStrings.Log_ManagerWriteTimeout);
        }
        catch (Exception ex)
        {
            // 写入失败/断连：清理并回到等待，等待下一个客户端
            _log.LogWarning(ServiceStrings.Log_ManagerSessionError, ex.Message);
        }
        finally
        {
            _hub.Changed -= PulseChanged;
            _log.LogInformation(ServiceStrings.Log_ManagerDisconnected);
        }
    }

    private async Task WaitForTickOrChangeAsync(CancellationToken ct)
    {
        var signal = Volatile.Read(ref _changedSignal);
        var delay = Task.Delay(TickInterval, _time, ct);
        await Task.WhenAny(delay, signal.Task);

        // 换用新的信号源；在本次等待期间发生的变化最多让下一帧提前，不会丢失
        Volatile.Write(ref _changedSignal, NewSignal());
    }

    private void PulseChanged() =>
        Volatile.Read(ref _changedSignal).TrySetResult(true);

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task WriteLineAsync(NamedPipeServerStream pipe, string line, CancellationToken ct)
    {
        // 写入期限：客户端卡死（不读）时即便仍有缓冲也不无限等待，保证单客户端可被顶替
        using var timeout = new CancellationTokenSource(WriteTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        var bytes = System.Text.Encoding.UTF8.GetBytes(line);
        await pipe.WriteAsync(bytes, linked.Token);
        await pipe.FlushAsync(linked.Token);
    }

    private NamedPipeServerStream CreatePipe() =>
        NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.Out,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 4096,
            outBufferSize: 4096,
            _security);

    // 生产 DACL：仅 SYSTEM 与内建管理员组完全控制，其他主体不可连接
    internal static PipeSecurity CreateDefaultSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }
}

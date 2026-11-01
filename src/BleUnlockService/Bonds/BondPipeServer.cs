using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using BleUnlockService.Resources;

namespace BleUnlockService.Bonds;

/// <summary>
/// \\.\pipe\BleUnlockBleBonds：把 Windows 蓝牙配对库中的设备（含 IRK）提供给管理端。
/// 服务以 SYSTEM 运行才能读取该注册表键；管道 DACL 只放行 SYSTEM 与管理员组。
/// 请求/响应均为单行 JSON；单连接可连续处理多个请求，空闲超时后回收。
/// 任何故障只记日志，绝不冒泡到服务主流程。
/// </summary>
public sealed class BondPipeServer : BackgroundService
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RebuildDelay = TimeSpan.FromSeconds(2);
    private const int MaxRequestChars = 4096;

    private readonly IBondedDeviceSource _source;
    private readonly ILogger<BondPipeServer> _log;
    private readonly string _pipeName;
    private readonly PipeSecurity _security;

    public BondPipeServer(
        IBondedDeviceSource source,
        ILogger<BondPipeServer> log,
        string pipeName = BondProtocol.PipeName)
        : this(source, log, pipeName, CreateDefaultSecurity())
    {
    }

    // 仅供测试替换 DACL（生产路径恒为 SYSTEM + Administrators 完全控制）
    internal BondPipeServer(
        IBondedDeviceSource source,
        ILogger<BondPipeServer> log,
        string pipeName,
        PipeSecurity security)
    {
        _source = source;
        _log = log;
        _pipeName = pipeName;
        _security = security;
    }

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
                // 建立/处理会话失败不能杀死循环；重建前稍作退避
                _log.LogWarning(ServiceStrings.Log_BondPipeError, ex.Message);
                try { await Task.Delay(RebuildDelay, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task RunSessionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(
                pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096, leaveOpen: true);
            using var writer = new StreamWriter(
                pipe, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true)
            {
                AutoFlush = true
            };

            while (!ct.IsCancellationRequested)
            {
                string? line;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(ReadTimeout);
                    try
                    {
                        line = await reader.ReadLineAsync(timeout.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        break; // 客户端空闲超时：回收会话
                    }
                }

                if (line is null)
                {
                    break; // 客户端断开
                }

                var response = HandleLine(line);
                await writer.WriteLineAsync(
                    JsonSerializer.Serialize(response, BondProtocol.JsonOptions));
            }
        }
        catch (OperationCanceledException)
        {
            // 停服取消是正常路径
        }
        catch (IOException)
        {
            // 客户端提前断开：回到等待下一个连接
        }
        catch (Exception ex)
        {
            _log.LogWarning(ServiceStrings.Log_BondSessionError, ex.Message);
        }
    }

    private BondResponse HandleLine(string line)
    {
        if (line.Length > MaxRequestChars)
        {
            return BondProtocol.Error(0, ServiceStrings.Bonds_RequestTooLong);
        }

        BondRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<BondRequest>(line, BondProtocol.JsonOptions);
        }
        catch (JsonException)
        {
            return BondProtocol.Error(0, ServiceStrings.Bonds_RequestUnparseable);
        }

        if (request is null || !string.Equals(request.T, "req", StringComparison.Ordinal))
        {
            return BondProtocol.Error(request?.Id ?? 0, ServiceStrings.Bonds_RequestUnparseable);
        }

        if (!string.Equals(request.Op, "list", StringComparison.Ordinal))
        {
            return BondProtocol.Error(request.Id, string.Format(ServiceStrings.Bonds_UnknownOperation, request.Op));
        }

        var result = _source.ReadAll();
        if (result.Error is not null)
        {
            return BondProtocol.Error(request.Id, result.Error);
        }

        _log.LogDebug(ServiceStrings.Log_BondsRead, result.Devices.Count);
        return BondProtocol.OkList(request.Id, result.Devices);
    }

    private NamedPipeServerStream CreatePipe() =>
        NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
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

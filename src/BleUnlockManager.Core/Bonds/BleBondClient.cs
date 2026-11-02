using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using BleUnlockManager.Core.Resources;

namespace BleUnlockManager.Core.Bonds;

/// <summary>管理端看到的已配对 BLE 设备（IRK 由服务从 Windows 配对库读取）。</summary>
public sealed record BleBondDevice(string Address, string? Name, string Irk)
{
    /// <summary>下拉框显示文本：名字（地址）；没有名字时只显示地址。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? Address
        : string.Format(CoreStrings.Bond_NameWithAddress, Name, Address);

    /// <summary>
    /// 能否用于解锁：只有配对时交换了身份密钥（IRK）的设备才能在随机地址下被认出。
    /// 没有 IRK 的设备服务也会列出（IRK 为空），管理端显示为「不支持」。
    /// </summary>
    public bool IsSupported => Irk.Length > 0;
}

/// <summary>读取结果；<c>Error</c> 非空表示本次读取失败（可直接呈现给用户）。</summary>
public sealed record BleBondListResult(IReadOnlyList<BleBondDevice> Devices, string? Error)
{
    public bool Success => Error is null;
}

/// <summary>
/// 与服务端 <c>BondProtocol</c> 对称的本地管道定义（v1，单行 UTF-8 JSON、camelCase）。
/// 注意：两侧协议字段必须保持一致（服务端另有测试锁定响应形状）。
/// </summary>
public static class BleBondProtocol
{
    public const int Version = 1;
    public const string PipeName = "BleUnlockBleBonds";

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

/// <summary>
/// 读取「Windows 已配对 BLE 设备」的一次性客户端：连接服务管道 → 发送 list 请求 → 读回响应。
/// 服务未运行/未安装时返回可读错误而不是抛异常，调用方直接显示即可。
/// </summary>
public sealed class BleBondClient
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    private readonly string _pipeName;

    public BleBondClient(string? pipeName = null)
    {
        _pipeName = pipeName ?? BleBondProtocol.PipeName;
    }

    public async Task<BleBondListResult> ListAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(effectiveTimeout);

        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            await pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);

            using var writer = new StreamWriter(
                pipe, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true)
            {
                AutoFlush = true
            };
            using var reader = new StreamReader(
                pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096, leaveOpen: true);

            var request = new BondRequest("req", 1, "list");
            await writer.WriteLineAsync(
                JsonSerializer.Serialize(request, BleBondProtocol.JsonOptions)).ConfigureAwait(false);

            var line = await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
            {
                return new BleBondListResult([], CoreStrings.Bond_NoData);
            }

            BondResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<BondResponse>(line, BleBondProtocol.JsonOptions);
            }
            catch (JsonException)
            {
                return new BleBondListResult([], CoreStrings.Bond_Unparseable);
            }

            if (response is null || !string.Equals(response.T, "res", StringComparison.Ordinal))
            {
                return new BleBondListResult([], CoreStrings.Bond_InvalidResponse);
            }

            if (!response.Ok)
            {
                return new BleBondListResult([], response.Error ?? CoreStrings.Bond_ServiceError);
            }

            var devices = (response.Devices ?? [])
                // IRK 为空 = 设备没有身份密钥（不支持），仍保留以便向导说明原因
                .Where(d => !string.IsNullOrWhiteSpace(d.Addr))
                .Select(d => new BleBondDevice(d.Addr.Trim(), d.Name, (d.Irk ?? "").Trim().ToUpperInvariant()))
                .ToList();

            return new BleBondListResult(devices, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new BleBondListResult([], CoreStrings.Bond_Timeout);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new BleBondListResult([], string.Format(CoreStrings.Bond_AccessDenied, ex.Message));
        }
        catch (IOException ex)
        {
            return new BleBondListResult([], string.Format(CoreStrings.Bond_ConnectFailed, ex.Message));
        }
        catch (Exception ex)
        {
            return new BleBondListResult([], string.Format(CoreStrings.Bond_ReadFailed, ex.Message));
        }
    }

    private sealed record BondRequest(string T, int Id, string Op);

    private sealed record BondResponse(
        string T,
        int Id,
        bool Ok,
        IReadOnlyList<BondDeviceFrame>? Devices,
        string? Error);

    private sealed record BondDeviceFrame(string Addr, string? Name, string? Irk);
}

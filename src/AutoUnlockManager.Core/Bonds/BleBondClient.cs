using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace AutoUnlockManager.Core.Bonds;

/// <summary>管理端看到的已配对 BLE 设备（IRK 由服务从 Windows 配对库读取）。</summary>
public sealed record BleBondDevice(string Address, string? Name, string Irk)
{
    /// <summary>下拉框显示文本：名字（地址）；没有名字时只显示地址。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? Address
        : $"{Name}（{Address}）";
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
    public const string PipeName = "AutoUnlockBleBonds";

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
                return new BleBondListResult([], "服务没有返回数据。");
            }

            BondResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<BondResponse>(line, BleBondProtocol.JsonOptions);
            }
            catch (JsonException)
            {
                return new BleBondListResult([], "服务返回了无法解析的数据。");
            }

            if (response is null || !string.Equals(response.T, "res", StringComparison.Ordinal))
            {
                return new BleBondListResult([], "服务返回了无效响应。");
            }

            if (!response.Ok)
            {
                return new BleBondListResult([], response.Error ?? "服务返回了错误。");
            }

            var devices = (response.Devices ?? [])
                .Where(d => !string.IsNullOrWhiteSpace(d.Addr) && !string.IsNullOrWhiteSpace(d.Irk))
                .Select(d => new BleBondDevice(d.Addr.Trim(), d.Name, d.Irk.Trim().ToUpperInvariant()))
                .ToList();

            return new BleBondListResult(devices, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new BleBondListResult([], "读取超时（服务可能未运行）。");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new BleBondListResult([], $"没有权限连接服务管道（需要管理员）：{ex.Message}");
        }
        catch (IOException ex)
        {
            return new BleBondListResult([], $"无法连接服务：{ex.Message}");
        }
        catch (Exception ex)
        {
            return new BleBondListResult([], $"读取已配对设备失败：{ex.Message}");
        }
    }

    private sealed record BondRequest(string T, int Id, string Op);

    private sealed record BondResponse(
        string T,
        int Id,
        bool Ok,
        IReadOnlyList<BondDeviceFrame>? Devices,
        string? Error);

    private sealed record BondDeviceFrame(string Addr, string? Name, string Irk);
}

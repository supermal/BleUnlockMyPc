using System.Text.Json;

namespace AutoUnlockService.Bonds;

/// <summary>
/// 管理端「已配对 BLE 设备」本地管道协议（v1）：单行 UTF-8 JSON、camelCase。
/// 请求：{"t":"req","id":1,"op":"list"}；响应：{"t":"res","id":1,"ok":true,"devices":[...]}。
/// IRK 属于敏感信息，只在本机 SYSTEM↔管理员之间的管道中传输，不写日志。
/// </summary>
public static class BondProtocol
{
    public const int Version = 1;
    public const string PipeName = "AutoUnlockBleBonds";

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string SerializeLine<T>(T frame) =>
        JsonSerializer.Serialize(frame, JsonOptions) + "\n";

    public static BondResponse OkList(int id, IReadOnlyList<BondedBleDevice> devices) =>
        new("res", id, true, devices
            .Select(d => new BondDeviceFrame(d.Address, d.Name, d.Irk))
            .ToList(), null);

    public static BondResponse Error(int id, string message) =>
        new("res", id, false, null, message);
}

/// <summary>请求帧（t 恒为 "req"；当前只支持 op="list"）。</summary>
public sealed record BondRequest(string T, int Id, string Op);

/// <summary>响应帧；失败时 <c>Devices</c> 为 null、<c>Error</c> 为可读原因。</summary>
public sealed record BondResponse(
    string T,
    int Id,
    bool Ok,
    IReadOnlyList<BondDeviceFrame>? Devices,
    string? Error);

/// <summary>设备条目：地址为规范化 MAC，Irk 为 32 位大写十六进制。</summary>
public sealed record BondDeviceFrame(string Addr, string? Name, string Irk);

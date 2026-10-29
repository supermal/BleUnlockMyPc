using Windows.Devices.Bluetooth;

namespace AutoUnlockManager.Services.Ble;

/// <summary>电脑扫描到的、可连接的 BLE 外设（只保留广播了名字的设备，便于用户识别）。</summary>
public sealed class NearbyBleDevice
{
    public required ulong AddressValue { get; init; }
    public bool IsRandomAddress { get; init; }
    public required string Address { get; init; }
    public string? Name { get; init; }
    public short Rssi { get; init; }
    public DateTimeOffset LastSeen { get; init; }

    public string DisplayName
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Name) ? "（未命名）" : Name.Trim();
            return $"{name}    {Address}    {Rssi} dBm";
        }
    }
}

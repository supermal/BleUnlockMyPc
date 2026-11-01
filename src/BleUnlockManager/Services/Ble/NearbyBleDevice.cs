using BleUnlockManager.Core.Resources;
using Windows.Devices.Bluetooth;

namespace BleUnlockManager.Services.Ble;

/// <summary>电脑扫描到的、可连接的 BLE 外设（只保留广播了名字的设备，便于用户识别）。</summary>
public sealed class NearbyBleDevice
{
    public required ulong AddressValue { get; init; }
    public bool IsRandomAddress { get; init; }
    public required string Address { get; init; }
    public string? Name { get; init; }
    public short Rssi { get; init; }
    public DateTimeOffset LastSeen { get; init; }

    /// <summary>Nombre para la lista; sin nombre, el texto «sin nombre» del idioma actual.</summary>
    public string ShownName => string.IsNullOrWhiteSpace(Name) ? UiStrings.Pair_UnnamedDevice : Name.Trim();

    public string DisplayName => $"{ShownName}    {Address}    {Rssi} dBm";
}

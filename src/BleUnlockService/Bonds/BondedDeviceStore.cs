using System.Security;
using System.Text;
using Microsoft.Win32;
using BleUnlockService.Resources;

namespace BleUnlockService.Bonds;

/// <summary>Windows 蓝牙绑定库中的一台 LE 设备（含用于解析随机地址的 IRK）。</summary>
public sealed record BondedBleDevice(string Address, string? Name, string Irk)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Address : Name;
}

/// <summary>配对库读取结果；<c>Error</c> 非空表示本次读取失败（错误文本可直接呈现）。</summary>
public sealed record BondedDeviceReadResult(IReadOnlyList<BondedBleDevice> Devices, string? Error);

/// <summary>供管道与测试替换的读取接口。</summary>
public interface IBondedDeviceSource
{
    BondedDeviceReadResult ReadAll();
}

/// <summary>
/// 读取 Windows 蓝牙绑定库：HKLM\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Keys。
/// 该键仅 SYSTEM 可读（管理员通常也被拒绝），服务以 LocalSystem 运行，因此由服务读取后
/// 经本地管道提供给管理端；IRK 属于敏感信息，绝不写日志。
/// </summary>
public sealed class WindowsBondedDeviceSource : IBondedDeviceSource
{
    private const string KeysPath =
        @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Keys";

    private const string DevicesPath =
        @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices";

    public BondedDeviceReadResult ReadAll()
    {
        try
        {
            using var keys = Registry.LocalMachine.OpenSubKey(KeysPath);
            if (keys is null)
            {
                return new BondedDeviceReadResult(Array.Empty<BondedBleDevice>(),
                    string.Format(ServiceStrings.Bonds_RegistryKeyNotFound, KeysPath));
            }

            // 同一设备可能出现在多个适配器下：按地址去重，优先保留有名字的条目
            var devices = new Dictionary<string, BondedBleDevice>(StringComparer.OrdinalIgnoreCase);

            foreach (var adapterName in keys.GetSubKeyNames())
            {
                using var adapter = TryOpenSubKey(keys, adapterName);
                if (adapter is null)
                {
                    continue;
                }

                foreach (var deviceName in adapter.GetSubKeyNames())
                {
                    using var device = TryOpenSubKey(adapter, deviceName);
                    if (device is null)
                    {
                        continue;
                    }

                    if (device.GetValue("IRK") is not byte[] irk || !IsUsableIrk(irk))
                    {
                        continue;
                    }

                    var address = NormalizeAddress(deviceName);
                    var name = ReadDeviceName(address) ?? DecodeRegistryName(device.GetValue("Name"));
                    var entry = new BondedBleDevice(address, name, Convert.ToHexString(irk));

                    if (!devices.TryGetValue(address, out var existing) ||
                        (string.IsNullOrWhiteSpace(existing.Name) && !string.IsNullOrWhiteSpace(name)))
                    {
                        devices[address] = entry;
                    }
                }
            }

            var ordered = devices.Values
                .OrderBy(d => d.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(d => d.Address, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new BondedDeviceReadResult(ordered, null);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new BondedDeviceReadResult(Array.Empty<BondedBleDevice>(),
                string.Format(ServiceStrings.Bonds_AccessDenied, ex.Message));
        }
        catch (SecurityException ex)
        {
            return new BondedDeviceReadResult(Array.Empty<BondedBleDevice>(),
                string.Format(ServiceStrings.Bonds_ReadFailed, ex.Message));
        }
        catch (Exception ex)
        {
            return new BondedDeviceReadResult(Array.Empty<BondedBleDevice>(),
                string.Format(ServiceStrings.Bonds_ReadFailedTyped, ex.GetType().Name, ex.Message));
        }
    }

    private static RegistryKey? TryOpenSubKey(RegistryKey parent, string name)
    {
        try
        {
            return parent.OpenSubKey(name);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadDeviceName(string address)
    {
        try
        {
            using var devices = Registry.LocalMachine.OpenSubKey(DevicesPath);
            if (devices is null)
            {
                return null;
            }

            // 键名可能带/不带分隔符、大小写不一；与 BleHealthIrk 的读取方式保持一致
            foreach (var candidate in AddressVariants(address))
            {
                using var device = devices.OpenSubKey(candidate);
                if (device is null)
                {
                    continue;
                }

                var name = DecodeRegistryName(device.GetValue("Name"));
                if (name is not null)
                {
                    return name;
                }
            }
        }
        catch
        {
            // 名字仅用于展示，读取失败不影响 IRK
        }

        return null;
    }

    private static IEnumerable<string> AddressVariants(string address)
    {
        var compact = address.Replace(":", string.Empty).Replace("-", string.Empty);
        yield return compact;
        yield return compact.ToLowerInvariant();
        yield return address;
        yield return address.ToLowerInvariant();

        if (compact.Length == 12)
        {
            var colonized = string.Join(":", Enumerable.Range(0, 6)
                .Select(i => compact.Substring(i * 2, 2)));
            yield return colonized;
            yield return colonized.ToLowerInvariant();
        }
    }

    /// <summary>
    /// BTHPORT 的设备名通常以 REG_BINARY UTF-8（尾部 NUL）保存；也兼容字符串/字符串数组。
    /// </summary>
    public static string? DecodeRegistryName(object? value)
    {
        switch (value)
        {
            case null:
                return null;

            case string text:
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();

            case byte[] bytes:
                var decoded = bytes.Length == 0 ? string.Empty : Encoding.UTF8.GetString(bytes);
                decoded = decoded.TrimEnd('\0', ' ').Trim();
                return string.IsNullOrWhiteSpace(decoded) ? null : decoded;

            case string[] parts:
                var joined = string.Join(" ", parts).Trim();
                return string.IsNullOrWhiteSpace(joined) ? null : joined;

            default:
                return null;
        }
    }

    /// <summary>IRK 必须是 16 字节且不是全 0 / 全 FF（未分发 IRK 的占位值）。</summary>
    public static bool IsUsableIrk(byte[]? irk)
    {
        if (irk is null || irk.Length != 16)
        {
            return false;
        }

        var allZero = true;
        var allFf = true;
        foreach (var b in irk)
        {
            if (b != 0x00) allZero = false;
            if (b != 0xFF) allFf = false;
        }

        return !allZero && !allFf;
    }

    /// <summary>把注册表键名规范化为 AA:BB:CC:DD:EE:FF（无法识别时原样返回）。</summary>
    public static string NormalizeAddress(string value)
    {
        var compact = value.Replace(":", string.Empty).Replace("-", string.Empty).Trim();
        if (compact.Length != 12 || !compact.All(Uri.IsHexDigit))
        {
            return value.Trim();
        }

        return string.Join(":", Enumerable.Range(0, 6)
            .Select(i => compact.Substring(i * 2, 2).ToUpperInvariant()));
    }
}

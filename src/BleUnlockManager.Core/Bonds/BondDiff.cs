namespace BleUnlockManager.Core.Bonds;

/// <summary>
/// 配对库差集：向导在配对前后各读一次 Windows 配对库，找出新增设备或 IRK 变化的设备
/// （同一设备重新配对时地址可能不变，但 IRK 会更新）。地址/IRK 比较忽略大小写与分隔符。
/// </summary>
public static class BondDiff
{
    /// <summary>返回 <paramref name="current"/> 中相对 <paramref name="baseline"/> 新增或 IRK 变化的条目。</summary>
    public static IReadOnlyList<BleBondDevice> FindNewOrChanged(
        IEnumerable<BleBondDevice> baseline,
        IEnumerable<BleBondDevice> current)
    {
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in baseline)
        {
            var key = AddressKey(device.Address);
            if (key.Length == 0)
            {
                continue;
            }

            known[key] = IrkKey(device.Irk);
        }

        var result = new List<BleBondDevice>();
        foreach (var device in current)
        {
            var key = AddressKey(device.Address);
            if (key.Length == 0)
            {
                continue;
            }

            if (!known.TryGetValue(key, out var oldIrk) ||
                !string.Equals(oldIrk, IrkKey(device.Irk), StringComparison.OrdinalIgnoreCase))
            {
                result.Add(device);
            }
        }

        return result;
    }

    /// <summary>把地址归一化为 12 位大写十六进制（用于差集/查找）；无法识别时返回大写去空格原文。</summary>
    public static string AddressKey(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return "";
        }

        var hex = new string(address.Where(Uri.IsHexDigit).ToArray());
        return hex.Length == 12 ? hex.ToUpperInvariant() : address.Trim().ToUpperInvariant();
    }

    /// <summary>按归一化地址查找设备（忽略大小写与冒号/横线差异）。</summary>
    public static BleBondDevice? FindByAddress(IEnumerable<BleBondDevice> devices, string? address)
    {
        var key = AddressKey(address);
        if (key.Length == 0)
        {
            return null;
        }

        return devices.FirstOrDefault(d =>
            string.Equals(AddressKey(d.Address), key, StringComparison.OrdinalIgnoreCase));
    }

    private static string IrkKey(string? irk)
    {
        if (string.IsNullOrWhiteSpace(irk))
        {
            return "";
        }

        var hex = new string(irk.Where(Uri.IsHexDigit).ToArray());
        return hex.ToUpperInvariant();
    }
}

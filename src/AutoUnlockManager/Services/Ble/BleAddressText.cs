using System.Globalization;
using System.Text;

namespace AutoUnlockManager.Services.Ble;

/// <summary>48 位 BLE 地址的显示/解析（与配对库地址格式一致：AA:BB:CC:DD:EE:FF）。</summary>
public static class BleAddressText
{
    public static string Format(ulong address)
    {
        var parts = new string[6];
        for (var i = 0; i < 6; i++)
        {
            parts[i] = ((address >> (8 * (5 - i))) & 0xFF)
                .ToString("X2", CultureInfo.InvariantCulture);
        }

        return string.Join(':', parts);
    }

    public static bool TryParse(string? text, out ulong address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var hex = new StringBuilder(12);
        foreach (var c in text)
        {
            if (Uri.IsHexDigit(c))
            {
                hex.Append(c);
            }
        }

        return hex.Length == 12 &&
               ulong.TryParse(hex.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
    }
}

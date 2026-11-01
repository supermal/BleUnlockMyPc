using System.Globalization;

namespace BleUnlockService.Core;

// Tipo de direccion BLE segun los dos bits mas significativos
public enum AddressKind { Public, RandomStatic, Resolvable, NonResolvable }

public static class BleAddress
{
    // Formatea una direccion de 48 bits como AA:BB:CC:DD:EE:FF (byte mas significativo primero)
    public static string Format(ulong address)
    {
        var parts = new string[6];
        for (int i = 0; i < 6; i++)
            parts[i] = ((address >> (8 * (5 - i))) & 0xFF).ToString("X2");
        return string.Join(':', parts);
    }

    // Acepta "AA:BB:CC:DD:EE:FF", "AA-BB-CC-DD-EE-FF" o 12 digitos hexadecimales seguidos
    public static bool TryParse(string? text, out ulong address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string hex = text.Trim().Replace(":", "").Replace("-", "");
        if (hex.Length != 12) return false;
        return ulong.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out address);
    }

    public static AddressKind Classify(ulong address, bool isRandom)
    {
        if (!isRandom) return AddressKind.Public;
        return ((address >> 46) & 0b11) switch
        {
            0b11 => AddressKind.RandomStatic,
            0b01 => AddressKind.Resolvable,
            0b00 => AddressKind.NonResolvable,
            // 0b10 esta reservado por la especificacion; lo tratamos como estatica
            _ => AddressKind.RandomStatic
        };
    }
}

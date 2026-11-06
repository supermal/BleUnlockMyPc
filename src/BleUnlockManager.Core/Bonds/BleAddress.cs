namespace BleUnlockManager.Core.Bonds;

/// <summary>
/// Tipo de dirección BLE: solo una dirección que no cambia sirve para seguir un dispositivo por su MAC
/// (sin IRK). Bluetooth Core, vol. 6, parte B, 1.3: en una dirección aleatoria, los dos bits altos dicen
/// si es estática (11), privada resoluble (01, cambia cada pocos minutos) o privada no resoluble (00).
/// </summary>
public static class BleAddress
{
    /// <summary>
    /// true si la dirección se mantiene: pública, o aleatoria estática. Las privadas cambian y con ellas
    /// el seguimiento por MAC perdería el dispositivo.
    /// </summary>
    public static bool IsStable(ulong address, bool isRandom)
    {
        if (!isRandom) return true;
        var topBits = (address >> 46) & 0b11;
        return topBits == 0b11;
    }

    /// <summary>Forma visible «AA:BB:CC:DD:EE:FF» de una dirección escrita de cualquier manera; null si no es válida.</summary>
    public static string? Display(string? raw)
    {
        if (Config.ConfigValidator.NormalizeAddress(raw) is not { } value) return null;
        var hex = value.ToString("X12", System.Globalization.CultureInfo.InvariantCulture);
        return string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }
}

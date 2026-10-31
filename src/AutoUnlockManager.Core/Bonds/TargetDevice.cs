using AutoUnlockManager.Core.Config;
using AutoUnlockManager.Core.Resources;

namespace AutoUnlockManager.Core.Bonds;

/// <summary>Nombre visible del dispositivo configurado, buscado por IRK en el almacén de emparejamientos.</summary>
public static class TargetDevice
{
    public static string NotSelected => CoreStrings.Device_NotSelected;
    public static string Unknown => CoreStrings.Device_Unknown;

    public static string Resolve(IReadOnlyList<BleBondDevice> bonds, string? irkPlain)
    {
        var irk = irkPlain?.Trim() ?? "";
        if (irk.Length == 0 || string.Equals(irk, ConfigValidator.NoDeviceIrk, StringComparison.OrdinalIgnoreCase))
        {
            return NotSelected;
        }

        var device = bonds.FirstOrDefault(d => string.Equals(d.Irk, irk, StringComparison.OrdinalIgnoreCase));
        return device switch
        {
            null => Unknown,
            { Name: { Length: > 0 } name } => name,
            _ => device.Address,
        };
    }
}

using BleUnlockManager.Core.Config;
using BleUnlockManager.Core.Resources;

namespace BleUnlockManager.Core.Bonds;

/// <summary>
/// Nombre visible del dispositivo configurado, buscado por IRK en el almacén de emparejamientos; sin IRK,
/// por la dirección (dispositivo seguido por su MAC).
/// </summary>
public static class TargetDevice
{
    public static string NotSelected => CoreStrings.Device_NotSelected;
    public static string Unknown => CoreStrings.Device_Unknown;

    public static string Resolve(IReadOnlyList<BleBondDevice> bonds, string? irkPlain, string? address = null)
    {
        var irk = irkPlain?.Trim() ?? "";
        if (irk.Length == 0 || string.Equals(irk, ConfigValidator.NoDeviceIrk, StringComparison.OrdinalIgnoreCase))
        {
            // El marcador de «sin dispositivo» borra también la dirección; solo queda el seguimiento por MAC
            if (irk.Length > 0 || BleAddress.Display(address) is not { } formatted)
            {
                return NotSelected;
            }

            return BondDiff.FindByAddress(bonds, formatted) is { Name: { Length: > 0 } bondName }
                ? bondName
                : formatted;
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

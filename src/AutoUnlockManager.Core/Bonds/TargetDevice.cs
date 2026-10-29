using AutoUnlockManager.Core.Config;

namespace AutoUnlockManager.Core.Bonds;

/// <summary>Nombre visible del dispositivo configurado, buscado por IRK en el almacén de emparejamientos.</summary>
public static class TargetDevice
{
    public const string NotSelected = "未选择设备";
    public const string Unknown = "未知设备";

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

using BleUnlockManager.Core;
using BleUnlockManager.Core.Bonds;
using BleUnlockManager.Core.Config;

namespace BleUnlockManager.Services;

/// <summary>
/// Nombre y dirección del dispositivo configurado: se cruza el IRK del fichero de configuración
/// con el almacén de emparejamientos de Windows (leído por el servicio). Se refresca al iniciar,
/// tras guardar la configuración y tras emparejar o desemparejar.
/// </summary>
public sealed partial class TargetDeviceService : ObservableObject
{
    private readonly BleBondClient _client = new();

    public TargetDeviceService()
    {
        DeviceName = "—";
        DeviceAddress = "";
    }

    [ObservableProperty]
    public partial string DeviceName { get; set; }

    [ObservableProperty]
    public partial string DeviceAddress { get; set; }

    public async Task RefreshAsync()
    {
        try
        {
            var config = await Task.Run(() => ConfigDocument.Load(AppPaths.ConfigFile));
            var irk = ConfigSecrets.DecryptForCompare(config.Device.Irk);
            var result = await _client.ListAsync();
            var bonds = result.Devices;

            DeviceName = TargetDevice.Resolve(bonds, irk);
            DeviceAddress = bonds.FirstOrDefault(d => string.Equals(d.Irk, irk, StringComparison.OrdinalIgnoreCase))?.Address
                ?? config.Device.IdentityAddress
                ?? "";
        }
        catch
        {
            // Sin acceso al fichero o sin servicio: se muestra como desconocido, nunca se interrumpe la UI
            DeviceName = TargetDevice.Unknown;
            DeviceAddress = "";
        }
    }
}

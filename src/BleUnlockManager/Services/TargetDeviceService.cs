using BleUnlockManager.Core;
using BleUnlockManager.Core.Bonds;
using BleUnlockManager.Core.Config;
using BleUnlockManager.Core.Localization;

namespace BleUnlockManager.Services;

/// <summary>
/// Nombre y dirección del dispositivo configurado: se cruza el IRK del fichero de configuración
/// con el almacén de emparejamientos de Windows (leído por el servicio). Se refresca al iniciar,
/// tras guardar la configuración y tras emparejar o desemparejar.
/// </summary>
public sealed partial class TargetDeviceService : ObservableObject
{
    private readonly BleBondClient _client = new();

    // Ultimo resultado sin traducir: «sin seleccionar» y «desconocido» se vuelven a escribir en el
    // idioma nuevo al cambiarlo; los nombres reales del dispositivo no se traducen
    private IReadOnlyList<BleBondDevice> _bonds = [];
    private string? _irk;
    private bool _failed;
    private bool _resolved;

    public TargetDeviceService()
    {
        DeviceName = "—";
        DeviceAddress = "";
        ManagerLanguage.Changed += OnLanguageChanged;
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

            (_bonds, _irk, _failed, _resolved) = (bonds, irk, false, true);
            DeviceName = TargetDevice.Resolve(bonds, irk);
            DeviceAddress = bonds.FirstOrDefault(d => string.Equals(d.Irk, irk, StringComparison.OrdinalIgnoreCase))?.Address
                ?? config.Device.IdentityAddress
                ?? "";
        }
        catch
        {
            // Sin acceso al fichero o sin servicio: se muestra como desconocido, nunca se interrumpe la UI
            (_bonds, _irk, _failed, _resolved) = ([], null, true, true);
            DeviceName = TargetDevice.Unknown;
            DeviceAddress = "";
        }
    }

    // Se recalcula con los datos ya leídos; no hace falta volver a pedir la lista al servicio
    private void OnLanguageChanged()
    {
        if (!_resolved)
        {
            return;
        }

        var name = _failed ? TargetDevice.Unknown : TargetDevice.Resolve(_bonds, _irk);
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            DeviceName = name;
        }
        else
        {
            dispatcher.BeginInvoke(() => DeviceName = name);
        }
    }
}

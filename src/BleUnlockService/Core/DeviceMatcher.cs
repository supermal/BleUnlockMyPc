using BleUnlockService.Ble;
using BleUnlockService.Config;

namespace BleUnlockService.Core;

// Unica pregunta que le interesa a la maquina de estados: esta emision es del dispositivo objetivo?
public sealed class DeviceMatcher
{
    private readonly RpaResolver _resolver = new();
    private readonly object _lock = new();
    private ulong? _identityAddress;
    private bool _hasKey;

    public IrkByteOrder? MatchedOrder => _resolver.MatchedOrder;

    public void Configure(DeviceOptions device)
    {
        var normalized = OptionsValidator.NormalizeIrk(device.Irk);
        lock (_lock)
        {
            _identityAddress = OptionsValidator.NormalizeAddress(device.IdentityAddress);
            _hasKey = normalized is not null;
        }
        if (normalized is not null)
            _resolver.SetKey(Convert.FromHexString(normalized), device.IrkByteOrder);
    }

    public bool IsTarget(AdvertisementSample sample)
    {
        ulong? identity;
        bool hasKey;
        lock (_lock)
        {
            identity = _identityAddress;
            hasKey = _hasKey;
        }

        // La direccion de identidad se comprueba primero: Windows puede reportarla
        // directamente en dispositivos ya emparejados, y entonces no hay RPA que resolver
        if (identity is { } id && sample.Address == id) return true;
        if (!hasKey) return false;

        return _resolver.Matches(sample.Address, sample.IsRandomAddress);
    }
}

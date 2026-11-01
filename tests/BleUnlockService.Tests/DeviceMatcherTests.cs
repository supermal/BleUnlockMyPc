using BleUnlockService.Ble;
using BleUnlockService.Config;
using BleUnlockService.Core;
using Xunit;

namespace BleUnlockService.Tests;

public class DeviceMatcherTests
{
    private const string SpecIrkHex = "ec0234a357c8ad05341010a60a397d9b";
    private const ulong SpecRpa = 0x708194_0DFBAAUL;
    private const ulong Identity = 0xAABBCCDDEEFFUL;

    private static AdvertisementSample Sample(ulong address, bool isRandom) =>
        new(address, isRandom, -50, DateTimeOffset.UnixEpoch);

    [Fact]
    public void IsTarget_reconoce_la_rpa_por_irk()
    {
        var m = new DeviceMatcher();
        m.Configure(new DeviceOptions { Irk = SpecIrkHex, IrkByteOrder = IrkByteOrder.Auto });

        Assert.True(m.IsTarget(Sample(SpecRpa, isRandom: true)));
    }

    [Fact]
    public void IsTarget_reconoce_la_direccion_de_identidad_sin_resolver_rpa()
    {
        var m = new DeviceMatcher();
        m.Configure(new DeviceOptions { Irk = SpecIrkHex, IdentityAddress = "AA:BB:CC:DD:EE:FF" });

        // Direccion publica: nunca podria resolverse como RPA, debe coincidir por identidad
        Assert.True(m.IsTarget(Sample(Identity, isRandom: false)));
    }

    [Fact]
    public void IsTarget_sin_identity_address_solo_usa_irk()
    {
        var m = new DeviceMatcher();
        m.Configure(new DeviceOptions { Irk = SpecIrkHex, IdentityAddress = null });

        Assert.False(m.IsTarget(Sample(Identity, isRandom: false)));
        Assert.True(m.IsTarget(Sample(SpecRpa, isRandom: true)));
    }

    [Fact]
    public void IsTarget_rechaza_dispositivo_ajeno()
    {
        var m = new DeviceMatcher();
        m.Configure(new DeviceOptions { Irk = SpecIrkHex, IdentityAddress = "AA:BB:CC:DD:EE:FF" });

        Assert.False(m.IsTarget(Sample(0x112233445566UL, isRandom: true)));
    }

    [Fact]
    public void Configure_con_irk_invalida_no_reconoce_nada()
    {
        var m = new DeviceMatcher();
        m.Configure(new DeviceOptions { Irk = "no-es-hexadecimal" });

        Assert.False(m.IsTarget(Sample(SpecRpa, isRandom: true)));
    }

    [Fact]
    public void Configure_cambia_el_dispositivo_seguido()
    {
        var m = new DeviceMatcher();
        m.Configure(new DeviceOptions { Irk = SpecIrkHex });
        Assert.True(m.IsTarget(Sample(SpecRpa, isRandom: true)));

        m.Configure(new DeviceOptions { Irk = "ec0234a357c8ad05341010a60a397d9a" });
        Assert.False(m.IsTarget(Sample(SpecRpa, isRandom: true)));
    }
}

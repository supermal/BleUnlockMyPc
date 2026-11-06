using BleUnlockManager.Core.Bonds;
using BleUnlockManager.Core.Config;
using Xunit;

namespace BleUnlockManager.Tests;

public sealed class TargetDeviceTests
{
    private static readonly BleBondDevice[] Bonds =
    [
        new("55:A2:DC:A6:30:DB", "Apple Watch", "00112233445566778899AABBCCDDEEFF"),
        new("59:93:E1:AA:B7:89", null, "FFEEDDCCBBAA99887766554433221100"),
    ];

    [Fact]
    public void Resolve_MatchingIrk_ReturnsName()
        => Assert.Equal("Apple Watch", TargetDevice.Resolve(Bonds, "00112233445566778899AABBCCDDEEFF"));

    [Fact]
    public void Resolve_IgnoresIrkCase()
        => Assert.Equal("Apple Watch", TargetDevice.Resolve(Bonds, "00112233445566778899aabbccddeeff"));

    [Fact]
    public void Resolve_DeviceWithoutName_ReturnsAddress()
        => Assert.Equal("59:93:E1:AA:B7:89", TargetDevice.Resolve(Bonds, "FFEEDDCCBBAA99887766554433221100"));

    [Fact]
    public void Resolve_NoDevicePlaceholder_ReturnsNotSelected()
        => Assert.Equal("未选择设备", TargetDevice.Resolve(Bonds, ConfigValidator.NoDeviceIrk));

    [Fact]
    public void Resolve_EmptyIrk_ReturnsNotSelected()
        => Assert.Equal("未选择设备", TargetDevice.Resolve(Bonds, ""));

    [Fact]
    public void Resolve_UnknownIrk_ReturnsUnknown()
        => Assert.Equal("未知设备", TargetDevice.Resolve(Bonds, "0123456789ABCDEF0123456789ABCDEF"));
}

// Seguimiento por dirección MAC (dispositivos sin IRK con dirección fija)
public sealed class MacTrackingTests
{
    private static readonly BleBondDevice[] Bonds =
    [
        new("55:A2:DC:A6:30:DB", "Apple Watch", "00112233445566778899AABBCCDDEEFF"),
        new("C8:47:8C:12:34:56", "Xiaomi Smart Band 10", ""),
    ];

    [Fact]
    public void Sin_IRK_se_busca_el_nombre_por_la_direccion()
        => Assert.Equal("Xiaomi Smart Band 10", TargetDevice.Resolve(Bonds, "", "c8-47-8c-12-34-56"));

    [Fact]
    public void Sin_IRK_y_sin_emparejar_se_muestra_la_direccion()
        => Assert.Equal("11:22:33:44:55:66", TargetDevice.Resolve(Bonds, "", "112233445566"));

    [Fact]
    public void El_marcador_de_sin_dispositivo_gana_a_la_direccion()
        => Assert.Equal("未选择设备", TargetDevice.Resolve(Bonds, ConfigValidator.NoDeviceIrk, "C8:47:8C:12:34:56"));

    [Fact]
    public void Sin_IRK_ni_direccion_no_hay_dispositivo()
        => Assert.Equal("未选择设备", TargetDevice.Resolve(Bonds, "", null));

    [Fact]
    public void Un_dispositivo_sin_IRK_se_marca_como_seguido_por_MAC()
    {
        var band = Bonds[1];
        Assert.True(band.TracksByAddress);
        Assert.False(band.IsSupported);
        Assert.Equal("Xiaomi Smart Band 10（C8:47:8C:12:34:56） · 按 MAC 地址", band.DisplayName);
        Assert.False(Bonds[0].TracksByAddress);
    }

    [Fact]
    public void Una_entrada_sin_direccion_muestra_solo_el_nombre()
        => Assert.Equal("手动填写 MAC 地址…", new BleBondDevice("", "手动填写 MAC 地址…", "", Paired: false).DisplayName);

    [Theory]
    [InlineData(0xC8478C123456UL, false, true)]   // pública
    [InlineData(0xC8478C123456UL, true, true)]    // aleatoria estática (bits altos 11)
    [InlineData(0x58478C123456UL, true, false)]   // privada resoluble (01): cambia
    [InlineData(0x18478C123456UL, true, false)]   // privada no resoluble (00): cambia
    public void Solo_las_direcciones_fijas_sirven_para_seguir_por_MAC(ulong address, bool isRandom, bool stable)
        => Assert.Equal(stable, BleAddress.IsStable(address, isRandom));
}

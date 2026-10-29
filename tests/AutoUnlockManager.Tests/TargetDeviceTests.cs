using AutoUnlockManager.Core.Bonds;
using AutoUnlockManager.Core.Config;
using Xunit;

namespace AutoUnlockManager.Tests;

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

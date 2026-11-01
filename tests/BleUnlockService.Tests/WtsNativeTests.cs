using BleUnlockService.Session;
using Xunit;

namespace BleUnlockService.Tests;

public class WtsNativeTests
{
    [Fact]
    public void Flags_cero_significa_bloqueada()
    {
        Assert.True(WtsNative.MapSessionLockFlags(0));
    }

    [Fact]
    public void Flags_uno_significa_desbloqueada()
    {
        Assert.False(WtsNative.MapSessionLockFlags(1));
    }

    [Theory]
    [InlineData(0xFFFFFFFFL)]
    [InlineData(2L)]
    public void Flags_desconocidos_no_se_mapean(long flags)
    {
        Assert.Null(WtsNative.MapSessionLockFlags(flags));
    }
}

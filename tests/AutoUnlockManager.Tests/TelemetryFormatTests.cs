using AutoUnlockManager.Core;
using Xunit;

namespace AutoUnlockManager.Tests;

public sealed class TelemetryFormatTests
{
    [Theory]
    [InlineData("Unlocked", "已解锁")]
    [InlineData("PendingLock", "待锁定")]
    [InlineData("Snoozed", "已暂缓")]
    [InlineData("LockedWaitingDeparture", "已锁定·等待离开")]
    [InlineData("LockedArmed", "已锁定·监控中")]
    [InlineData("UnlockPending", "解锁确认中")]
    [InlineData("Suspended", "已挂起")]
    [InlineData(null, "未知")]
    [InlineData("", "未知")]
    [InlineData("FutureState", "FutureState")]
    public void StateName_Mapping(string? name, string expected)
    {
        Assert.Equal(expected, TelemetryFormat.StateName(name));
    }

    [Fact]
    public void Dbm_NullAndValue()
    {
        Assert.Equal("—", TelemetryFormat.Dbm(null));
        Assert.Equal("-57.3 dBm", TelemetryFormat.Dbm(-57.34));
        Assert.Equal("-45.0 dBm", TelemetryFormat.Dbm(-45));
    }

    [Fact]
    public void Duration_Boundaries()
    {
        Assert.Equal("45 秒", TelemetryFormat.Duration(TimeSpan.FromSeconds(45)));
        Assert.Equal("3 分 12 秒", TelemetryFormat.Duration(TimeSpan.FromSeconds(192)));
        Assert.Equal("1 小时 5 分", TelemetryFormat.Duration(TimeSpan.FromMinutes(65)));
        Assert.Equal("0 秒", TelemetryFormat.Duration(TimeSpan.FromMilliseconds(-100)));
    }

    [Fact]
    public void Since_UsesServerClock()
    {
        Assert.Equal("—", TelemetryFormat.Since(null, 10_000));
        Assert.Equal("1 分 0 秒", TelemetryFormat.Since(1_000, 61_000));
        Assert.Equal("0 秒", TelemetryFormat.Since(70_000, 61_000)); // 时钟回拨保护
    }

    [Fact]
    public void Uptime_Formats()
    {
        Assert.Equal("1 分 1 秒", TelemetryFormat.Uptime(61.9));
        Assert.Equal("0 秒", TelemetryFormat.Uptime(-5));
    }

    [Fact]
    public void LastMatch_Formats()
    {
        Assert.Equal("从未", TelemetryFormat.LastMatch(null));
        Assert.Equal("42 秒前", TelemetryFormat.LastMatch(42_000));
        Assert.Equal("8 分钟前", TelemetryFormat.LastMatch(8 * 60_000L));
        Assert.Equal("2 小时前", TelemetryFormat.LastMatch(2 * 3_600_000L));
        Assert.Equal("59 秒前", TelemetryFormat.LastMatch(59_600)); // 向下取整：不出现「60 秒前」
        Assert.Equal("59 分钟前", TelemetryFormat.LastMatch(3_599_000));
    }

    [Fact]
    public void IsLastMatchStale_Rules()
    {
        Assert.False(TelemetryFormat.IsLastMatchStale(null, 200));
        Assert.True(TelemetryFormat.IsLastMatchStale(null, 400));
        Assert.False(TelemetryFormat.IsLastMatchStale(299_000, 999_999));
        Assert.True(TelemetryFormat.IsLastMatchStale(301_000, 999_999));
    }

    [Theory]
    [InlineData(null, ScanHealth.Unknown)]
    [InlineData("", ScanHealth.Unknown)]
    [InlineData("running", ScanHealth.Running)]
    [InlineData("radioOff", ScanHealth.Degraded)]
    [InlineData("noAdapter", ScanHealth.Degraded)]
    [InlineData("startFailed", ScanHealth.Degraded)]
    // El texto (que depende del idioma del servicio) ya no decide nada
    [InlineData("BLE 扫描已启动", ScanHealth.Degraded)]
    public void ScanHealth_Classification(string? code, ScanHealth expected)
    {
        Assert.Equal(expected, TelemetryFormat.ClassifyScan(code));
    }
}

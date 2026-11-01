using BleUnlockManager.Core.Telemetry;
using Xunit;

namespace BleUnlockManager.Tests;

public sealed class SignalZoneTests
{
    [Theory]
    [InlineData(-51, SignalZone.Hold)]
    [InlineData(-45, SignalZone.Near)]
    [InlineData(-30, SignalZone.Near)]
    [InlineData(-80, SignalZone.Away)]
    [InlineData(-95, SignalZone.Away)]
    [InlineData(-79.9, SignalZone.Hold)]
    public void Classify_UsesInclusiveThresholds(double dbm, SignalZone expected)
        => Assert.Equal(expected, SignalZones.Classify(dbm, lockDbm: -80, unlockDbm: -45));

    [Theory]
    [InlineData(SignalZone.Near, "靠近区")]
    [InlineData(SignalZone.Hold, "保持区")]
    [InlineData(SignalZone.Away, "离开区")]
    public void DisplayName_MatchesDesignCopy(SignalZone zone, string expected)
        => Assert.Equal(expected, SignalZones.DisplayName(zone));
}

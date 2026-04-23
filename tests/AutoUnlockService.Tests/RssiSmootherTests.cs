using AutoUnlockService;
using Xunit;

namespace AutoUnlockService.Tests;

public class RssiSmootherTests
{
    [Fact]
    public void SingleValue_ReturnsThatValue()
    {
        var s = new RssiSmoother(5);
        Assert.Equal(-65.0, s.Add(-65));
    }

    [Fact]
    public void TwoValues_ReturnsAverage()
    {
        var s = new RssiSmoother(5);
        s.Add(-60);
        Assert.Equal(-65.0, s.Add(-70));
    }

    [Fact]
    public void FullWindow_OldestDropped()
    {
        var s = new RssiSmoother(3);
        s.Add(-60);
        s.Add(-70);
        s.Add(-80);
        double result = s.Add(-90);
        // Buffer after 4th Add: [-70, -80, -90] => avg = -80
        Assert.Equal(-80.0, result, 10);
    }

    [Fact]
    public void Reset_StartsOver()
    {
        var s = new RssiSmoother(3);
        s.Add(-60);
        s.Add(-70);
        s.Reset();
        Assert.Equal(-50.0, s.Add(-50));
    }
}

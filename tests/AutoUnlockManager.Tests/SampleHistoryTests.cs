using AutoUnlockManager.Core.Management;
using Xunit;

namespace AutoUnlockManager.Tests;

public sealed class SampleHistoryTests
{
    [Fact]
    public void AddRange_SortsByTime()
    {
        var history = new SampleHistory();
        history.AddRange([[2000, -50], [1000, -60], [1500, -55]]);

        var points = history.Snapshot();
        Assert.Equal(3, points.Count);
        Assert.Equal(1000, points[0].TsMs);
        Assert.Equal(1500, points[1].TsMs);
        Assert.Equal(2000, points[2].TsMs);
    }

    [Fact]
    public void AddRange_SkipsMalformedRows()
    {
        var history = new SampleHistory();
        history.AddRange([[], [1000], [2000, -50]]);

        Assert.Equal(1, history.Count);
        Assert.Equal(2000, history.Latest?.TsMs);
    }

    [Fact]
    public void AddRange_NullOrEmpty_NoOp()
    {
        var history = new SampleHistory();
        history.AddRange(null);
        history.AddRange([]);

        Assert.Equal(0, history.Count);
        Assert.Null(history.Latest);
    }

    [Fact]
    public void Prune_DropsOutsideRetention()
    {
        var history = new SampleHistory(TimeSpan.FromMinutes(5));
        history.AddRange([[0, -60], [400_000, -60], [600_000, -60]]);

        history.Prune(610_000); // cutoff = 310_000

        var points = history.Snapshot();
        Assert.Equal(2, points.Count);
        Assert.Equal(400_000, points[0].TsMs);
    }

    [Fact]
    public void Clear_Empties()
    {
        var history = new SampleHistory();
        history.AddRange([[1000, -50]]);
        history.Clear();

        Assert.Equal(0, history.Count);
        Assert.Null(history.Latest);
    }

    [Fact]
    public void Downsample_UnderLimit_ReturnsAll()
    {
        var history = new SampleHistory();
        for (var i = 0; i < 300; i++)
        {
            history.AddRange([[i * 1000L, -50]]);
        }

        Assert.Equal(300, history.Downsample(600).Count);
    }

    [Fact]
    public void Downsample_OverLimit_BoundedAndKeepsExtremes()
    {
        var history = new SampleHistory();
        var expectedMin = double.MaxValue;
        var expectedMax = double.MinValue;
        for (var i = 0; i < 2000; i++)
        {
            long value = (long)Math.Round(-50 + 10 * Math.Sin(i / 40.0));
            expectedMin = Math.Min(expectedMin, value);
            expectedMax = Math.Max(expectedMax, value);
            history.AddRange([[i * 1000L, value]]);
        }

        var downsampled = history.Downsample(600);

        Assert.True(downsampled.Count <= 600, $"count={downsampled.Count}");
        Assert.True(downsampled.Count > 0);
        Assert.Equal(expectedMin, downsampled.Min(p => p.Rssi));
        Assert.Equal(expectedMax, downsampled.Max(p => p.Rssi));

        // 时间单调不降（截断到 0.1 避免浮点噪声）
        for (var i = 1; i < downsampled.Count; i++)
        {
            Assert.True(downsampled[i].TsMs >= downsampled[i - 1].TsMs);
        }
    }

    [Fact]
    public void AddRange_EnforcesCapacityLimit()
    {
        var history = new SampleHistory();
        var total = SampleHistory.MaxCapacity + 500;
        var rows = new long[total][];
        for (var i = 0; i < total; i++)
        {
            rows[i] = [i, -50];
        }

        history.AddRange(rows);

        Assert.Equal(SampleHistory.MaxCapacity, history.Count);
        // 丢最旧：首条应恰为 total - MaxCapacity
        Assert.Equal(total - SampleHistory.MaxCapacity, history.Snapshot()[0].TsMs);
    }
}

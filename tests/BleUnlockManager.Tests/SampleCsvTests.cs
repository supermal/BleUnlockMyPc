using BleUnlockManager.Core;
using BleUnlockManager.Core.Management;
using Xunit;

namespace BleUnlockManager.Tests;

public sealed class SampleCsvTests
{
    [Fact]
    public void Build_Empty_HeaderOnly()
    {
        var csv = SampleCsv.Build([]);

        Assert.Equal("timestamp_iso,rssi_dbm\r\n", csv);
    }

    [Fact]
    public void Build_Rows_WithIsoUtcAndValues()
    {
        var csv = SampleCsv.Build(
        [
            new SamplePoint(0, -61),
            new SamplePoint(1760647976250, -59.55),
        ]);

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal("timestamp_iso,rssi_dbm", lines[0]);
        Assert.Equal("1970-01-01T00:00:00.000Z,-61", lines[1]);
        Assert.Equal("2025-10-16T20:52:56.250Z,-59.55", lines[2]);
    }
}

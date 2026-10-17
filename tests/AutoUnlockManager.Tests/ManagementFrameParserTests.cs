using AutoUnlockManager.Core.Management;
using Xunit;

namespace AutoUnlockManager.Tests;

public sealed class ManagementFrameParserTests
{
    [Fact]
    public void ParseHello_Valid()
    {
        var frame = ManagementFrameParser.Parse("""{"t":"hello","v":1,"svc":"1.4.2"}""");

        var hello = Assert.IsType<HelloFrame>(frame);
        Assert.Equal(1, hello.V);
        Assert.Equal("1.4.2", hello.Svc);
    }

    [Fact]
    public void ParseSnapshot_DesignSample()
    {
        const string line = """
            {"t":"snapshot","v":1,"seq":813,"gen":2,"tsMs":1760648000123,"uptimeSec":532.4,
             "state":{"name":"LockedArmed","sinceMs":1760647911250},
             "scan":{"status":"BLE 扫描已启动","detail":null},
             "helper":{"connected":true},"cred":{"connected":true},
             "thr":{"lockDbm":-65,"unlockDbm":-45,"lockWinSec":10,"unlockWinSec":5,"minSamples":3},
             "avg":{"w5":-57.3,"w10":-58.1,"n5":6,"n10":14},
             "lastMatchMs":420,
             "samples":[[1760647975000,-61],[1760647976250,-59]]}
            """;

        var frame = ManagementFrameParser.Parse(line);

        var snapshot = Assert.IsType<SnapshotFrame>(frame);
        Assert.Equal(813, snapshot.Seq);
        Assert.Equal(2, snapshot.Gen);
        Assert.Equal(1760648000123, snapshot.TsMs);
        Assert.Equal(532.4, snapshot.UptimeSec);
        Assert.Equal("LockedArmed", snapshot.State?.Name);
        Assert.Equal(1760647911250, snapshot.State?.SinceMs);
        Assert.Equal("BLE 扫描已启动", snapshot.Scan?.Status);
        Assert.True(snapshot.Helper?.Connected);
        Assert.True(snapshot.Cred?.Connected);
        Assert.Equal(-65, snapshot.Thr?.LockDbm);
        Assert.Equal(-45, snapshot.Thr?.UnlockDbm);
        Assert.Equal(10, snapshot.Thr?.LockWinSec);
        Assert.Equal(-57.3, snapshot.Avg?.W5);
        Assert.Equal(6, snapshot.Avg?.N5);
        Assert.Equal(420, snapshot.LastMatchMs);
        Assert.Equal(2, snapshot.Samples?.Length);
        Assert.Equal(1760647976250, snapshot.Samples?[1][0]);
        Assert.Equal(-59, snapshot.Samples?[1][1]);
    }

    [Fact]
    public void ParseSnapshot_UnknownFieldsTolerated()
    {
        const string line = """
            {"t":"snapshot","v":1,"seq":1,"gen":1,"tsMs":1,"uptimeSec":1,
             "future":{"x":1},"extraTop":"ignored","samples":[[1000,-60]]}
            """;

        var frame = ManagementFrameParser.Parse(line);

        var snapshot = Assert.IsType<SnapshotFrame>(frame);
        Assert.Null(snapshot.State);
        Assert.Null(snapshot.Thr);
        Assert.Single(snapshot.Samples!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("not json {")]
    [InlineData("[1,2,3]")]
    [InlineData("\"text\"")]
    [InlineData("""{"x":1}""")]
    [InlineData("""{"t":123}""")]
    [InlineData("""{"t":"weird"}""")]
    [InlineData("{\"t\":\"snapshot\",\"seq\":")] // 截断 JSON（缺值）
    public void Parse_InvalidOrUnknown_ReturnsNull(string line)
    {
        Assert.Null(ManagementFrameParser.Parse(line));
    }

    [Fact]
    public void Parse_NullInput_ReturnsNull()
    {
        Assert.Null(ManagementFrameParser.Parse(null));
    }
}

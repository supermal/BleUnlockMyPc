using AutoUnlockManager.Core.Management;
using AutoUnlockManager.Core.Telemetry;
using Xunit;

namespace AutoUnlockManager.Tests;

public sealed class TelemetryModelTests
{
    private static SnapshotFrame Snapshot(
        long gen = 1,
        long tsMs = 10_000,
        long[][]? samples = null,
        string state = "Unlocked",
        SnapshotBatch? batch = null) =>
        new("snapshot", 1, 1, gen, tsMs, 60,
            new SnapshotState(state, 0),
            new SnapshotScan("BLE 扫描已启动", null),
            new SnapshotConnection(true),
            new SnapshotConnection(false),
            new SnapshotThresholds(-80, -45, 3, 5, 10),
            new SnapshotAverages(-52.4, -53.1, 5, 10),
            400,
            samples ?? [[9_000, -55], [9_500, -51]],
            batch);

    [Fact]
    public void Initial_IsConnectingWithNoSignal()
    {
        var model = new TelemetryModel();

        Assert.False(model.Connected);
        Assert.Null(model.CurrentDbm);
        Assert.Null(model.Zone);
        Assert.Equal(TrayState.Connecting, model.TrayState);
    }

    [Fact]
    public void ApplySnapshot_ExposesLatestDbmZoneAndBatch()
    {
        var model = new TelemetryModel { ServiceRunning = true };
        var batch = new SnapshotBatch("leave", 3, 5);

        model.Apply(Snapshot(batch: batch));

        Assert.True(model.Connected);
        Assert.Equal(-51, model.CurrentDbm);
        Assert.Equal(SignalZone.Hold, model.Zone);
        Assert.Equal(batch, model.Batch);
        Assert.Equal(TrayState.Leaving, model.TrayState);
        Assert.Equal(2, model.History.Count);
    }

    [Fact]
    public void ApplySnapshot_SameGen_AppendsAndReportsNoReset()
    {
        var model = new TelemetryModel();
        model.Apply(Snapshot());

        var reset = model.Apply(Snapshot(samples: [[9_800, -50]]));

        Assert.False(reset);
        Assert.Equal(3, model.History.Count);
    }

    [Fact]
    public void ApplySnapshot_GenChange_ClearsHistory()
    {
        var model = new TelemetryModel();
        model.Apply(Snapshot(gen: 1));

        var reset = model.Apply(Snapshot(gen: 2, samples: [[9_900, -60]]));

        Assert.True(reset);
        Assert.Equal(1, model.History.Count);
        Assert.Equal(-60, model.CurrentDbm);
    }

    [Fact]
    public void Hello_ClearsHistoryAndSnapshot()
    {
        var model = new TelemetryModel();
        model.Apply(Snapshot());

        var reset = model.Apply(new HelloFrame("hello", 1, "1.0"));

        Assert.True(reset);
        Assert.Equal(0, model.History.Count);
        Assert.Null(model.Snapshot);
    }

    [Fact]
    public void Disconnect_ClearsSignalButKeepsHistory()
    {
        var model = new TelemetryModel { ServiceRunning = true };
        model.Apply(Snapshot(batch: new SnapshotBatch("leave", 1, 5)));

        model.SetConnected(false);

        Assert.False(model.Connected);
        Assert.Null(model.CurrentDbm);
        Assert.Null(model.Batch);
        Assert.Equal(2, model.History.Count);
        Assert.Equal(TrayState.Connecting, model.TrayState);
    }

    [Fact]
    public void ServiceStopped_TrayStateIsStopped()
    {
        var model = new TelemetryModel { ServiceRunning = false };

        Assert.Equal(TrayState.Stopped, model.TrayState);
    }

    [Fact]
    public void Tooltip_UsesDeviceNameAndCurrentSignal()
    {
        var model = new TelemetryModel { ServiceRunning = true };
        model.Apply(Snapshot());

        var tip = model.Tooltip("Apple Watch");

        Assert.Equal("AutoUnlock：已解锁", tip.Line1);
        Assert.Equal("Apple Watch -51 dBm，最近匹配 0 秒前", tip.Line2);
    }
}

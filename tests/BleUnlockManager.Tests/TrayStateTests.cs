using BleUnlockManager.Core.Management;
using BleUnlockManager.Core.Telemetry;
using Xunit;

namespace BleUnlockManager.Tests;

public sealed class TrayStateTests
{
    private static readonly SnapshotBatch Leave3Of5 = new("leave", 3, 5);

    [Fact]
    public void Resolve_ServiceStopped_IsStopped()
        => Assert.Equal(TrayState.Stopped, TrayStates.Resolve(false, false, null, null));

    [Fact]
    public void Resolve_ServiceUnknownAndDisconnected_IsConnecting()
        => Assert.Equal(TrayState.Connecting, TrayStates.Resolve(null, false, null, null));

    [Fact]
    public void Resolve_ServiceRunningButNotConnected_IsConnecting()
        => Assert.Equal(TrayState.Connecting, TrayStates.Resolve(true, false, "Unlocked", null));

    [Fact]
    public void Resolve_Suspended_IsSuspended()
        => Assert.Equal(TrayState.Suspended, TrayStates.Resolve(true, true, "Suspended", null));

    [Fact]
    public void Resolve_LeaveBatch_IsLeaving()
        => Assert.Equal(TrayState.Leaving, TrayStates.Resolve(true, true, "Unlocked", Leave3Of5));

    [Fact]
    public void Resolve_PendingLock_IsLeaving()
        => Assert.Equal(TrayState.Leaving, TrayStates.Resolve(true, true, "PendingLock", null));

    [Fact]
    public void Resolve_UnlockBatch_IsRunning()
        => Assert.Equal(TrayState.Running, TrayStates.Resolve(true, true, "LockedArmed", new SnapshotBatch("unlock", 1, 3)));

    [Fact]
    public void Resolve_UnlockedWithoutBatch_IsRunning()
        => Assert.Equal(TrayState.Running, TrayStates.Resolve(true, true, "Unlocked", null));

    [Fact]
    public void Tooltip_Running_ShowsStateAndSignal()
    {
        var tip = TrayStates.Tooltip(TrayState.Running, "Unlocked", "Apple Watch", -51.3, -80, "0.4 秒前", null);

        Assert.Equal(new TrayTooltip("BleUnlock：已解锁", "Apple Watch -51 dBm，最近匹配 0.4 秒前"), tip);
    }

    [Fact]
    public void Tooltip_Leaving_ShowsBatchAndThreshold()
    {
        var tip = TrayStates.Tooltip(TrayState.Leaving, "Unlocked", "Apple Watch", -84, -80, "0.2 秒前", Leave3Of5);

        Assert.Equal(new TrayTooltip("BleUnlock：离开确认 3/5", "Apple Watch -84 dBm，低于锁定阈值 -80"), tip);
    }

    [Fact]
    public void Tooltip_Stopped_TellsHowToRecover()
    {
        var tip = TrayStates.Tooltip(TrayState.Stopped, null, "Apple Watch", null, null, null, null);

        Assert.Equal(new TrayTooltip("BleUnlock：服务已停止", "右键选择「启动服务」即可恢复"), tip);
    }

    [Fact]
    public void Tooltip_Suspended_AsksToCheckPassword()
        => Assert.Equal("BleUnlock：自动解锁已暂停，请检查密码",
            TrayStates.Tooltip(TrayState.Suspended, "Suspended", "Apple Watch", -50, -80, null, null).Line1);

    [Fact]
    public void Tooltip_Connecting_SaysConnecting()
        => Assert.Equal("BleUnlock：正在连接服务…",
            TrayStates.Tooltip(TrayState.Connecting, null, "Apple Watch", null, null, null, null).Line1);

    [Fact]
    public void Tooltip_LongDeviceName_StaysWithinWindowsLimit()
    {
        var tip = TrayStates.Tooltip(TrayState.Running, "Unlocked", new string('设', 200), -51, -80, "0.4 秒前", null);

        Assert.True(tip.ToString().Length <= 127);
        Assert.StartsWith("BleUnlock：已解锁\n", tip.ToString());
    }
}

using AutoUnlockService;
using Xunit;

namespace AutoUnlockService.Tests;

public class ProximityStateMachineTests
{
    private ProximityStateMachine Make(int unlockCount = 3, int lockCount = 5)
        => new(-60, -80, unlockCount, lockCount);

    [Fact]
    public void InitialState_IsLocked()
    {
        Assert.Equal(ProximityState.Locked, Make().State);
    }

    [Fact]
    public void BelowUnlockThreshold_NoStateChange()
    {
        var sm = Make();
        sm.Feed(-65); sm.Feed(-65); sm.Feed(-65);
        Assert.Equal(ProximityState.Locked, sm.State);
    }

    [Fact]
    public void ConsecutiveNear_TriggersUnlock()
    {
        var sm = Make(unlockCount: 3);
        bool fired = false;
        sm.UnlockRequested += () => fired = true;
        sm.Feed(-55); sm.Feed(-55); sm.Feed(-55);
        Assert.Equal(ProximityState.Unlocked, sm.State);
        Assert.True(fired);
    }

    [Fact]
    public void NearBrokenByDead_ResetsCount()
    {
        var sm = Make(unlockCount: 3);
        sm.Feed(-55); sm.Feed(-55);
        sm.Feed(-70); // dead zone resets consecutive
        sm.Feed(-55); sm.Feed(-55);
        Assert.Equal(ProximityState.Locked, sm.State);
    }

    [Fact]
    public void WhenUnlocked_ConsecutiveFar_TriggersLock()
    {
        var sm = Make(unlockCount: 3, lockCount: 5);
        bool lockFired = false;
        sm.LockRequested += () => lockFired = true;
        sm.Feed(-55); sm.Feed(-55); sm.Feed(-55); // unlock
        sm.Feed(-85); sm.Feed(-85); sm.Feed(-85); sm.Feed(-85); sm.Feed(-85); // 5x far
        Assert.Equal(ProximityState.Locked, sm.State);
        Assert.True(lockFired);
    }

    [Fact]
    public void UnlockNotFiredTwice_OnSameApproach()
    {
        var sm = Make(unlockCount: 3);
        int count = 0;
        sm.UnlockRequested += () => count++;
        for (int i = 0; i < 10; i++) sm.Feed(-55);
        Assert.Equal(1, count);
    }

    [Fact]
    public void DeadZone_DoesNotChangeLocked()
    {
        var sm = Make();
        sm.Feed(-70); sm.Feed(-70);
        Assert.Equal(ProximityState.Locked, sm.State);
    }

    [Fact]
    public void DeadZone_DoesNotChangeUnlocked()
    {
        var sm = Make(unlockCount: 1, lockCount: 5);
        sm.Feed(-55); // unlock with count=1
        sm.Feed(-70); sm.Feed(-70);
        Assert.Equal(ProximityState.Unlocked, sm.State);
    }
}

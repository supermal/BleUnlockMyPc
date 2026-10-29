using AutoUnlockManager.Core.Config;
using Xunit;

namespace AutoUnlockManager.Tests;

public sealed class ConfigChangeSummaryTests
{
    private static AutoUnlockConfig Baseline() => new()
    {
        Device = { Irk = "00112233445566778899AABBCCDDEEFF" },
        Credentials = { UserName = "user", Password = "secret" },
    };

    [Fact]
    public void Summarize_Empty_IsEmpty()
        => Assert.Equal("", ConfigDiff.Summarize([]));

    [Fact]
    public void Summarize_SingleChange_ShowsOldAndNew()
    {
        var edited = Baseline();
        edited.Proximity.LockConfirmSampleCount = 12;

        Assert.Equal("锁屏确认个数 10 → 12", ConfigDiff.Summarize(ConfigDiff.Compute(Baseline(), edited)));
    }

    [Fact]
    public void Summarize_Password_HidesValues()
    {
        var edited = Baseline();
        edited.Credentials.Password = "other";

        Assert.Equal("凭据密码 已修改", ConfigDiff.Summarize(ConfigDiff.Compute(Baseline(), edited)));
    }

    [Fact]
    public void Summarize_Irk_HidesKey()
    {
        var edited = Baseline();
        edited.Device.Irk = "FFEEDDCCBBAA99887766554433221100";

        var summary = ConfigDiff.Summarize(ConfigDiff.Compute(Baseline(), edited));

        Assert.EndsWith("已修改", summary);
        Assert.DoesNotContain("FFEEDD", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Summarize_Multiple_ShowsFirstAndCount()
    {
        var edited = Baseline();
        edited.Proximity.LockConfirmSampleCount = 12;
        edited.Notification.SnoozeMinutes = 10;
        edited.Proximity.SampleIntervalMs = 500;

        Assert.Equal("锁屏确认个数等 3 项", ConfigDiff.Summarize(ConfigDiff.Compute(Baseline(), edited)));
    }
}

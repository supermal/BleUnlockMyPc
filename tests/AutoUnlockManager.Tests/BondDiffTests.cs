using AutoUnlockManager.Core.Bonds;
using Xunit;

namespace AutoUnlockManager.Tests;

public sealed class BondDiffTests
{
    private static BleBondDevice Device(string address, string irk, string? name = null)
        => new(address, name, irk);

    [Fact]
    public void NewAddress_IsReported()
    {
        var baseline = new[] { Device("AA:BB:CC:DD:EE:01", "0011") };
        var current = new[]
        {
            Device("AA:BB:CC:DD:EE:01", "0011"),
            Device("AA:BB:CC:DD:EE:02", "0022"),
        };

        var diff = BondDiff.FindNewOrChanged(baseline, current);

        Assert.Single(diff);
        Assert.Equal("AA:BB:CC:DD:EE:02", diff[0].Address);
    }

    [Fact]
    public void ChangedIrk_OnSameAddress_IsReported()
    {
        var baseline = new[] { Device("AA:BB:CC:DD:EE:01", "0011") };
        var current = new[] { Device("AA:BB:CC:DD:EE:01", "0012") };

        var diff = BondDiff.FindNewOrChanged(baseline, current);

        Assert.Single(diff);
        Assert.Equal("0012", diff[0].Irk);
    }

    [Fact]
    public void AddressAndIrkFormatting_AreIgnored()
    {
        var baseline = new[] { Device("AA:BB:CC:DD:EE:01", "aa:bb") };
        var current = new[] { Device("aabbccddee01", "AABB") };

        Assert.Empty(BondDiff.FindNewOrChanged(baseline, current));
    }

    [Fact]
    public void EmptyBaseline_ReportsAllNamedAddresses()
    {
        var current = new[]
        {
            Device("", "0011", "synthetic"),
            Device("AA:BB:CC:DD:EE:01", "0011"),
        };

        var diff = BondDiff.FindNewOrChanged([], current);

        Assert.Single(diff);
    }

    [Fact]
    public void FindByAddress_IgnoresSeparatorsAndCase()
    {
        var devices = new[] { Device("AA:BB:CC:DD:EE:01", "0011", "watch") };

        var found = BondDiff.FindByAddress(devices, "aabbccddee01");

        Assert.NotNull(found);
        Assert.Equal("watch", found!.Name);
    }
}

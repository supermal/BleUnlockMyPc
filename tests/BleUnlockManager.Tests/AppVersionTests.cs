using BleUnlockManager.Core;
using Xunit;

namespace BleUnlockManager.Tests;

/// <summary>«设置 → 关于 → 版本号»: versión de MinVer más el commit corto (2026-11-03).</summary>
public sealed class AppVersionTests
{
    private const string Commit = "4880ccdf20eef877940413f7bcf97be827a9bd42";

    [Fact]
    public void Una_version_de_paquete_muestra_el_commit_corto()
    {
        var text = AppVersion.Display("1.0.1+" + Commit);

        Assert.StartsWith("1.0.1", text);
        Assert.Contains("4880ccd", text);
        Assert.DoesNotContain(Commit, text);
    }

    [Fact]
    public void Sin_commit_se_muestra_solo_la_version()
        => Assert.Equal("1.0.1", AppVersion.Display(" 1.0.1 "));

    [Fact]
    public void Una_version_preliminar_de_MinVer_se_marca_como_de_desarrollo()
    {
        var withCommit = AppVersion.Display("1.0.1-alpha.0.3+" + Commit);
        var withoutCommit = AppVersion.Display("0.0.0-alpha.0.112");

        Assert.StartsWith("1.0.1-alpha.0.3", withCommit);
        Assert.Contains("4880ccd", withCommit);
        Assert.NotEqual(AppVersion.Display("1.0.1+" + Commit).Replace("1.0.1", ""), withCommit.Replace("1.0.1-alpha.0.3", ""));
        Assert.NotEqual("0.0.0-alpha.0.112", withoutCommit);
    }

    [Theory]
    [InlineData("1.0.1+build.5")]
    [InlineData("1.0.1+")]
    public void Metadatos_que_no_son_un_commit_no_se_muestran(string informational)
        => Assert.Equal("1.0.1", AppVersion.Display(informational));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("+abc")]
    public void Sin_version_se_muestra_un_guion(string? informational)
        => Assert.Equal("—", AppVersion.Display(informational));
}

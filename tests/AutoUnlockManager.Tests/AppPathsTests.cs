using AutoUnlockManager.Core;
using Xunit;

namespace AutoUnlockManager.Tests;

/// <summary>
/// 锁定 AppPaths 的固定取值：这些路径是管理端与服务端（Program.cs 的 DataDir、
/// Serilog 日志布局）之间的契约，改动必须同步两侧。
/// </summary>
public class AppPathsTests
{
    [Fact]
    public void DataDir_es_ProgramData_mas_AutoUnlock()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AutoUnlock");

        Assert.Equal(expected, AppPaths.DataDir);
        Assert.True(Path.IsPathRooted(AppPaths.DataDir));
    }

    [Fact]
    public void ConfigFile_termina_en_AutoUnlock_appsettings_json()
    {
        Assert.EndsWith(@"AutoUnlock\appsettings.json", AppPaths.ConfigFile);
        Assert.Equal(Path.Combine(AppPaths.DataDir, "appsettings.json"), AppPaths.ConfigFile);
    }

    [Fact]
    public void LogsDir_cuelga_de_DataDir()
    {
        Assert.Equal(Path.Combine(AppPaths.DataDir, "logs"), AppPaths.LogsDir);
    }

    [Fact]
    public void LogFilePattern_es_autounlock_wildcard_log()
    {
        Assert.Equal("autounlock-*.log", AppPaths.LogFilePattern);
    }

    [Fact]
    public void ManagerAppDataDir_es_LocalAppData_mas_AutoUnlockManager()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoUnlockManager");

        Assert.Equal(expected, AppPaths.ManagerAppDataDir);
    }

    [Fact]
    public void ManagerSettingsFile_cuelga_de_ManagerAppDataDir()
    {
        Assert.Equal(Path.Combine(AppPaths.ManagerAppDataDir, "settings.json"), AppPaths.ManagerSettingsFile);
    }
}

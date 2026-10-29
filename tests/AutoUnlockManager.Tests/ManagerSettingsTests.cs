using AutoUnlockManager.Core;
using Xunit;

namespace AutoUnlockManager.Tests;

public sealed class ManagerSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aum-settings-" + Guid.NewGuid().ToString("N"));

    private string File => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Load_MissingFile_DefaultsToSystem()
        => Assert.Equal(ThemePreference.System, ManagerSettings.Load(File).Theme);

    [Fact]
    public void Load_WinUiFile_ReadsTheme()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File, """{"Theme":"Dark"}""");

        Assert.Equal(ThemePreference.Dark, ManagerSettings.Load(File).Theme);
    }

    [Fact]
    public void Load_CorruptFile_DefaultsToSystem()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File, "{not json");

        Assert.Equal(ThemePreference.System, ManagerSettings.Load(File).Theme);
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips_AndCreatesDirectory()
    {
        new ManagerSettings { Theme = ThemePreference.Light }.Save(File);

        Assert.Equal(ThemePreference.Light, ManagerSettings.Load(File).Theme);
        Assert.Contains("\"Theme\":\"Light\"", System.IO.File.ReadAllText(File));
    }
}

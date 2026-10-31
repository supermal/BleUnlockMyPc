using AutoUnlock.Shared;
using AutoUnlockManager.Core;
using AutoUnlockManager.Core.Config;
using AutoUnlockManager.Core.Localization;
using AutoUnlockManager.Core.Resources;
using Xunit;

namespace AutoUnlockManager.Tests.Localization;

[Collection(TestCulture.LanguageCollection)]
public sealed class ManagerLanguageTests : IDisposable
{
    public ManagerLanguageTests() => ManagerLanguage.Set(AppLanguage.Chinese);

    public void Dispose()
    {
        ManagerLanguage.Set(AppLanguage.Chinese);
        TestCulture.Reset();
    }

    [Fact]
    public void Set_changes_core_strings_and_raises_once()
    {
        var raised = 0;
        void Handler() => raised++;
        ManagerLanguage.Changed += Handler;
        try
        {
            ManagerLanguage.Set(AppLanguage.Spanish);
            ManagerLanguage.Set(AppLanguage.Spanish);

            Assert.Equal(AppLanguage.Spanish, ManagerLanguage.Current);
            Assert.Equal("es", CoreStrings.Culture?.Name);
            Assert.Equal(1, raised);
        }
        finally
        {
            ManagerLanguage.Changed -= Handler;
        }
    }

    [Fact]
    public void TelemetryFormat_follows_language()
    {
        ManagerLanguage.Set(AppLanguage.Spanish);
        Assert.Equal("Desbloqueado", TelemetryFormat.StateName("Unlocked"));

        ManagerLanguage.Set(AppLanguage.English);
        Assert.Equal("Unlocked", TelemetryFormat.StateName("Unlocked"));
    }

    [Fact]
    public void Validator_messages_follow_language()
    {
        ManagerLanguage.Set(AppLanguage.English);
        var config = new AutoUnlockConfig();
        config.Device.Irk = "nope";
        config.Credentials.UserName = "u";

        var errors = ConfigValidator.Validate(config).Errors;

        Assert.Contains(errors, e => e.Message.Contains("32 hexadecimal characters"));
    }
}

public sealed class ConfigDocumentLanguageTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("aum-lang-").FullName;

    private string Path_ => Path.Combine(_dir, "appsettings.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string Sample = """
        {
          "Global": { "Keep": true },
          "AutoUnlock": {
            "Device": { "Irk": "x", "Future": 1 },
            "Ui": { "Language": "es", "Other": "keep" }
          }
        }
        """;

    [Fact]
    public void ReadLanguage_missing_file_returns_system()
        => Assert.Equal("system", ConfigDocument.ReadLanguage(Path_));

    [Fact]
    public void ReadLanguage_reads_value()
    {
        File.WriteAllText(Path_, Sample);

        Assert.Equal("es", ConfigDocument.ReadLanguage(Path_));
    }

    [Fact]
    public void ReadLanguage_corrupt_file_returns_system()
    {
        File.WriteAllText(Path_, "{ not json");

        Assert.Equal("system", ConfigDocument.ReadLanguage(Path_));
    }

    [Fact]
    public void SaveLanguage_changes_only_the_language()
    {
        File.WriteAllText(Path_, Sample);

        ConfigDocument.SaveLanguage(Path_, "en");

        var expected = System.Text.Json.Nodes.JsonNode.Parse(Sample)!;
        expected["AutoUnlock"]!["Ui"]!["Language"] = "en";
        var actual = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path_))!;
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(expected, actual), actual.ToJsonString());
    }

    [Fact]
    public void SaveLanguage_creates_ui_section_when_missing()
    {
        File.WriteAllText(Path_, """{ "AutoUnlock": { "Device": { "Irk": "x" } } }""");

        ConfigDocument.SaveLanguage(Path_, "zh");

        Assert.Equal("zh", ConfigDocument.ReadLanguage(Path_));
    }

    [Fact]
    public void Save_does_not_touch_language()
    {
        File.WriteAllText(Path_, Sample);
        var config = ConfigDocument.Load(Path_);
        config.Device.Irk = "00112233445566778899aabbccddeeff";

        ConfigDocument.Save(Path_, config);

        Assert.Equal("es", ConfigDocument.ReadLanguage(Path_));
    }
}

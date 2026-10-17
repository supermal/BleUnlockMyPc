using System.Text.Json;
using System.Text.Json.Nodes;
using AutoUnlockManager.Core.Config;
using Xunit;

namespace AutoUnlockManager.Tests;

/// <summary>配置文件读写测试：容忍解析、原位改值保真、归一化、原子替换与备份。</summary>
public sealed class ConfigDocumentTests : IDisposable
{
    private const string SampleJson = """
        {
          "Global": { "Keep": true },
          "AutoUnlock": {
            "Extra": { "Marker": 42 },
            "Device": {
              "Irk": "AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99",
              "IrkByteOrder": "Reversed",
              "IdentityAddress": "aa-bb-cc-dd-ee-ff",
              "Future": "x"
            },
            "Credentials": { "UserName": "user1", "Password": "pw1" },
            "Proximity": {
              "LockThresholdDbm": -80,
              "LockWindowSeconds": 10,
              "UnlockThresholdDbm": -45,
              "UnlockWindowSeconds": 5,
              "MinSamples": 3,
              "StartupGraceSeconds": 15,
              "UnlockPendingTimeoutSeconds": 20
            },
            "Notification": {
              "Mode": "Toast",
              "PromptTimeoutSeconds": 15,
              "RejectSnoozeSeconds": 60,
              "SnoozeMinutes": 5
            }
          }
        }
        """;

    private readonly string _dir;

    public ConfigDocumentTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aum-config-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 清理失败不影响结论
        }
    }

    private string WriteFile(string content, string name = "appsettings.json")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    // —— Load ——

    [Fact]
    public void Load_FullFile_MapsAllValues()
    {
        var path = WriteFile(SampleJson);

        var config = ConfigDocument.Load(path);

        Assert.Equal("AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99", config.Device.Irk); // 加载保留原文
        Assert.Equal(IrkByteOrder.Reversed, config.Device.IrkByteOrder);
        Assert.Equal("aa-bb-cc-dd-ee-ff", config.Device.IdentityAddress);
        Assert.Equal("user1", config.Credentials.UserName);
        Assert.Equal(-80, config.Proximity.LockThresholdDbm);
        Assert.Equal(10, config.Proximity.LockWindowSeconds);
        Assert.Equal(-45, config.Proximity.UnlockThresholdDbm);
        Assert.Equal(5, config.Proximity.UnlockWindowSeconds);
        Assert.Equal(3, config.Proximity.MinSamples);
        Assert.Equal(15, config.Proximity.StartupGraceSeconds);
        Assert.Equal(20, config.Proximity.UnlockPendingTimeoutSeconds);
        Assert.Equal(NotifierMode.Toast, config.Notification.Mode);
        Assert.Equal(15, config.Notification.PromptTimeoutSeconds);
        Assert.Equal(60, config.Notification.RejectSnoozeSeconds);
        Assert.Equal(5, config.Notification.SnoozeMinutes);
    }

    [Fact]
    public void Load_MissingSection_ReturnsDefaults()
    {
        var path = WriteFile("{}");

        var config = ConfigDocument.Load(path);

        Assert.Equal("", config.Device.Irk);
        Assert.Equal(IrkByteOrder.Auto, config.Device.IrkByteOrder);
        Assert.Equal(-80, config.Proximity.LockThresholdDbm);
        Assert.Equal(NotifierMode.Toast, config.Notification.Mode);
    }

    [Fact]
    public void Load_CaseInsensitiveKeys_And_LowercaseEnums()
    {
        var path = WriteFile(
            """{"autounlock":{"device":{"irk":"00112233445566778899aabbccddeeff","irkbyteorder":"reversed"},"notification":{"mode":"dialog"}}}""");

        var config = ConfigDocument.Load(path);

        Assert.Equal(IrkByteOrder.Reversed, config.Device.IrkByteOrder);
        Assert.Equal(NotifierMode.Dialog, config.Notification.Mode);
    }

    [Fact]
    public void Load_StringNumbers_Comments_TrailingCommas()
    {
        var path = WriteFile("""
            {
              // 注释与尾逗号：与服务端配置提供程序语义一致
              "AutoUnlock": {
                "Device": { "Irk": "00112233445566778899aabbccddeeff" },
                "Credentials": { "UserName": "u" },
                "Proximity": { "LockWindowSeconds": "12.5", },
                "Notification": { "Mode": 1 }
              }
            }
            """);

        var config = ConfigDocument.Load(path);

        Assert.Equal(12.5, config.Proximity.LockWindowSeconds);
        Assert.Equal(NotifierMode.Dialog, config.Notification.Mode); // 数字枚举容错（typeConverter 语义）
    }

    [Fact]
    public void Load_InvalidJson_Throws()
        => Assert.ThrowsAny<JsonException>(() => ConfigDocument.Load(WriteFile("{ not json")));

    [Fact]
    public void Load_NonObjectRoot_Throws()
        => Assert.Throws<JsonException>(() => ConfigDocument.Load(WriteFile("[1, 2]")));

    [Fact]
    public void Load_MissingFile_Throws()
        => Assert.Throws<FileNotFoundException>(() => ConfigDocument.Load(Path.Combine(_dir, "nope.json")));

    // —— Save ——

    [Fact]
    public void Save_WritesAllValues_PreservesUnknownFields()
    {
        var path = WriteFile(SampleJson);
        var config = ConfigDocument.Load(path);
        config.Proximity.LockThresholdDbm = -75;
        config.Proximity.MinSamples = 4;
        config.Notification.Mode = NotifierMode.Dialog;
        config.Device.IdentityAddress = null;

        ConfigDocument.Save(path, config);

        var root = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.True(root["Global"]!["Keep"]!.GetValue<bool>());                  // 顶层未知节保留
        Assert.Equal(42, root["AutoUnlock"]!["Extra"]!["Marker"]!.GetValue<int>()); // 节内未知字段保留
        Assert.Equal("x", root["AutoUnlock"]!["Device"]!["Future"]!.GetValue<string>());

        var proximity = root["AutoUnlock"]!["Proximity"]!;
        Assert.Equal(JsonValueKind.Number, proximity["LockThresholdDbm"]!.GetValueKind());
        Assert.Equal(-75, proximity["LockThresholdDbm"]!.GetValue<double>());
        Assert.Equal(JsonValueKind.Number, proximity["MinSamples"]!.GetValueKind());
        Assert.Equal(4, proximity["MinSamples"]!.GetValue<int>());

        var notification = root["AutoUnlock"]!["Notification"]!;
        Assert.Equal(JsonValueKind.String, notification["Mode"]!.GetValueKind());
        Assert.Equal("Dialog", notification["Mode"]!.GetValue<string>());

        Assert.Null(root["AutoUnlock"]!["Device"]!["IdentityAddress"]); // 清空 → null
    }

    [Fact]
    public void Save_NormalizesIrk_And_Address()
    {
        var path = WriteFile(SampleJson);
        var config = ConfigDocument.Load(path);
        config.Device.Irk = "  0x00112233-4455-6677-8899-AABBCCDDEEFF ";
        config.Device.IdentityAddress = "AA BB CC DD EE FF";

        ConfigDocument.Save(path, config);

        var device = JsonNode.Parse(File.ReadAllText(path))!["AutoUnlock"]!["Device"]!;
        Assert.Equal("00112233445566778899aabbccddeeff", device["Irk"]!.GetValue<string>());
        Assert.Equal("aabbccddeeff", device["IdentityAddress"]!.GetValue<string>());
    }

    [Fact]
    public void Save_CamelCaseFile_ReusesExistingKeys_NoDuplicates()
    {
        var path = WriteFile("""
            {"autounlock":{"proximity":{"lockthresholddbm":-80,"LockWindowSeconds":10}}}
            """);
        var config = ConfigDocument.Load(path);
        config.Proximity.LockThresholdDbm = -70;

        ConfigDocument.Save(path, config);

        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        var autoUnlockKeys = root
            .Where(kvp => string.Equals(kvp.Key, "AutoUnlock", StringComparison.OrdinalIgnoreCase))
            .Count();
        Assert.Equal(1, autoUnlockKeys); // 不得新增同义大小写键

        var section = (JsonObject)root
            .First(kvp => string.Equals(kvp.Key, "AutoUnlock", StringComparison.OrdinalIgnoreCase))
            .Value!;
        var proximity = (JsonObject)section["proximity"]!;
        var lockThresholdKeys = proximity
            .Where(kvp => string.Equals(kvp.Key, "LockThresholdDbm", StringComparison.OrdinalIgnoreCase))
            .Count();
        Assert.Equal(1, lockThresholdKeys);
        Assert.Equal(-70, proximity["lockthresholddbm"]!.GetValue<double>()); // 原位更新（保留原键）
    }

    [Fact]
    public void Save_MissingSection_CreatesFullStructure()
    {
        var path = WriteFile("{}");
        var config = new AutoUnlockConfig();
        config.Device.Irk = "00112233445566778899aabbccddeeff";
        config.Credentials.UserName = "u";

        ConfigDocument.Save(path, config);

        var section = JsonNode.Parse(File.ReadAllText(path))!["AutoUnlock"]!;
        Assert.NotNull(section["Device"]);
        Assert.NotNull(section["Credentials"]);
        Assert.NotNull(section["Proximity"]);
        Assert.NotNull(section["Notification"]);
        Assert.Equal(-80, section["Proximity"]!["LockThresholdDbm"]!.GetValue<double>());
    }

    [Fact]
    public void Save_CreatesBackup_WithOriginalContent_CleansTemp()
    {
        var path = WriteFile(SampleJson);
        var original = File.ReadAllText(path);
        var config = ConfigDocument.Load(path);
        config.Proximity.LockWindowSeconds = 11;

        ConfigDocument.Save(path, config);

        Assert.Equal(original, File.ReadAllText(path + ".bak"));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(11, ConfigDocument.Load(path).Proximity.LockWindowSeconds);
    }

    [Fact]
    public void Save_SecondSave_UpdatesBackup()
    {
        var path = WriteFile(SampleJson);
        var first = ConfigDocument.Load(path);
        first.Proximity.LockWindowSeconds = 11;
        ConfigDocument.Save(path, first);
        var afterFirst = File.ReadAllText(path);

        var second = ConfigDocument.Load(path);
        second.Proximity.LockWindowSeconds = 12;
        ConfigDocument.Save(path, second);

        Assert.Equal(afterFirst, File.ReadAllText(path + ".bak"));
        Assert.Equal(12, ConfigDocument.Load(path).Proximity.LockWindowSeconds);
    }

    [Fact]
    public void Save_MissingFile_Throws()
        => Assert.Throws<FileNotFoundException>(() =>
            ConfigDocument.Save(Path.Combine(_dir, "nope.json"), new AutoUnlockConfig()));

    [Fact]
    public void Save_NonObjectRoot_Throws()
        => Assert.Throws<JsonException>(() =>
            ConfigDocument.Save(WriteFile("[1]"), new AutoUnlockConfig()));

    [Fact]
    public void Save_InvalidConfigValues_StillWriteRaw_IrkNotNormalizable()
    {
        // Save 不做业务校验（调用方先校验）：不可归一化的值原样写入，避免静默改写
        var path = WriteFile(SampleJson);
        var config = ConfigDocument.Load(path);
        config.Device.Irk = "not-an-irk";

        ConfigDocument.Save(path, config);

        Assert.Equal("not-an-irk", JsonNode.Parse(File.ReadAllText(path))!["AutoUnlock"]!["Device"]!["Irk"]!.GetValue<string>());
    }

    [Fact]
    public void Save_TargetLocked_ThrowsWithoutDamagingOriginal_TempCleaned()
    {
        // 评审建议 2：目标文件被只读共享占用 → 3 次重试后抛 IOException，原文与 .bak 不受损、.tmp 清理
        var path = WriteFile(SampleJson);
        var original = File.ReadAllText(path);
        var config = ConfigDocument.Load(path);
        config.Proximity.LockWindowSeconds = 11;

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<IOException>(() => ConfigDocument.Save(path, config));
        }

        Assert.Equal(original, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.False(File.Exists(path + ".bak"));
    }

    // —— Load：JSON null 归一（镜像服务端「null = 未设置」，评审 I1） ——

    [Fact]
    public void Load_NullStrings_NormalizedToEmpty_SaveWorks()
    {
        var path = WriteFile("""
            {"AutoUnlock":{"Device":{"Irk":null,"IdentityAddress":null},"Credentials":{"UserName":null,"Password":null}}}
            """);

        var config = ConfigDocument.Load(path);

        Assert.Equal("", config.Device.Irk);
        Assert.Null(config.Device.IdentityAddress);
        Assert.Equal("", config.Credentials.UserName);
        Assert.Equal("", config.Credentials.Password);

        ConfigDocument.Save(path, config); // 加载后保存路径不再遇 null（原 NRE 场景）
        Assert.Equal(3, ConfigDocument.Load(path).Proximity.MinSamples); // 缺省字段以默认值写回
    }

    [Fact]
    public void Load_NullSections_FallbackToDefaults_SaveWorks()
    {
        var path = WriteFile("""
            {"AutoUnlock":{"Device":null,"Credentials":null,"Proximity":null,"Notification":null}}
            """);

        var config = ConfigDocument.Load(path);

        Assert.Equal("", config.Device.Irk);
        Assert.Equal(-80, config.Proximity.LockThresholdDbm);
        Assert.Equal(NotifierMode.Toast, config.Notification.Mode);

        ConfigDocument.Save(path, config);
        Assert.Equal(10, ConfigDocument.Load(path).Proximity.LockWindowSeconds);
    }
}

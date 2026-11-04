using BleUnlockManager.Core.Config;
using BleUnlockService.Config;
using Xunit;

namespace BleUnlockManager.Tests;

/// <summary>校验镜像测试：规则与提示文案必须与服务端 OptionsValidator 逐字一致。</summary>
public sealed class ConfigValidatorTests
{
    private const string ValidIrk = "00112233445566778899AABBCCDDEEFF";

    private static BleUnlockConfig Valid()
    {
        var config = new BleUnlockConfig();
        config.Device.Irk = ValidIrk;
        config.Credentials.UserName = "test";
        config.Credentials.Password = "pw";
        return config;
    }

    // —— NormalizeIrk ——

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("00112233445566778899AABBCCDDEEF")]   // 31 位
    [InlineData("00112233445566778899AABBCCDDEEFFF")] // 33 位
    [InlineData("00112233445566778899AABBCCDDEEGZ")]  // 非十六进制
    public void NormalizeIrk_Invalid_ReturnsNull(string? raw)
        => Assert.Null(ConfigValidator.NormalizeIrk(raw));

    [Theory]
    [InlineData("00112233445566778899AABBCCDDEEFF")]
    [InlineData("00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF")]
    [InlineData("00-11-22-33-44-55-66-77-88-99-AA-BB-CC-DD-EE-FF")]
    [InlineData("  00 11 22 33 44 55 66 77 88 99 AA BB CC DD EE FF  ")]
    [InlineData("0x00112233445566778899AABBCCDDEEFF")]
    [InlineData("0X00112233445566778899AABBCCDDEEFF")]
    public void NormalizeIrk_Formatted_ReturnsLowerHex(string raw)
        => Assert.Equal("00112233445566778899aabbccddeeff", ConfigValidator.NormalizeIrk(raw));

    // —— NormalizeAddress ——

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("1122334455")]       // 过短
    [InlineData("11223344556677")]   // 过长
    [InlineData("0x112233445566")]   // 服务端 NormalizeAddress 不剥 0x 前缀（镜像）
    [InlineData("114433GG5566")]
    public void NormalizeAddress_Invalid_ReturnsNull(string? raw)
        => Assert.Null(ConfigValidator.NormalizeAddress(raw));

    [Theory]
    [InlineData("AABBCCDDEEFF", 0xAABBCCDDEEFFUL)]
    [InlineData("aa:bb:cc:dd:ee:ff", 0xAABBCCDDEEFFUL)]
    [InlineData("AA-BB-CC-DD-EE-FF", 0xAABBCCDDEEFFUL)]
    [InlineData("aa bb cc dd ee ff", 0xAABBCCDDEEFFUL)]
    public void NormalizeAddress_Formatted_ReturnsValue(string raw, ulong expected)
        => Assert.Equal(expected, ConfigValidator.NormalizeAddress(raw));

    [Fact]
    public void FormatAddress_NormalizedToLowerHex()
    {
        Assert.Equal("aabbccddeeff", ConfigValidator.FormatAddress(0xAABBCCDDEEFFUL));
        Assert.Null(ConfigValidator.FormatAddress(null));
    }

    // —— Validate：规则与文案逐字镜像 ——

    [Fact]
    public void Validate_ValidConfig_Ok()
    {
        var result = ConfigValidator.Validate(Valid());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_IrkInvalid_Message()
    {
        var config = Valid();
        config.Device.Irk = "xyz";

        var error = Assert.Single(ConfigValidator.Validate(config).Errors);
        Assert.Equal(ConfigFields.DeviceIrk, error.Field);
        Assert.Equal("Device.Irk 必须是 32 个十六进制字符", error.Message);
    }

    [Fact]
    public void Validate_ProtectedIrk_SkipsHexRule()
    {
        var config = Valid();
        config.Device.Irk = SecretProtector.Protect(ValidIrk); // 管理端看到的磁盘原文：密文不校验明文

        Assert.True(ConfigValidator.Validate(config).IsValid);
    }

    [Fact]
    public void Validate_IdentityAddressInvalid_Message()
    {
        var config = Valid();
        config.Device.IdentityAddress = "12345";

        var error = Assert.Single(ConfigValidator.Validate(config).Errors);
        Assert.Equal("Device.IdentityAddress 必须是合法的 6 字节 MAC 地址", error.Message);
    }

    [Fact]
    public void Validate_EmptyUserNameAndPassword_IsValid_LockOnly()
    {
        var config = Valid();
        config.Credentials.UserName = "  ";
        config.Credentials.Password = "";

        Assert.True(ConfigValidator.Validate(config).IsValid);
    }

    [Fact]
    public void Validate_FreshInstall_EmptyDeviceAndCredentials_IsValid()
        => Assert.True(ConfigValidator.Validate(new BleUnlockConfig()).IsValid);

    [Fact]
    public void Validate_MalformedIrk_StillAnError()
    {
        var config = Valid();
        config.Device.Irk = "abc";

        var error = Assert.Single(ConfigValidator.Validate(config).Errors);
        Assert.Equal(ConfigFields.DeviceIrk, error.Field);
    }

    [Fact]
    public void Validate_ControlCharacters_Messages()
    {
        var config = Valid();
        config.Credentials.UserName = "a\nb";
        config.Credentials.Password = "p\tw";

        var errors = ConfigValidator.Validate(config).Errors;
        Assert.Equal(2, errors.Count);
        Assert.Equal("Credentials.UserName 不能包含控制字符（换行、回车、制表符等）", errors[0].Message);
        Assert.Equal("Credentials.Password 不能包含控制字符（换行、回车、制表符等）", errors[1].Message);
    }

    [Theory]
    [InlineData(-45.0, -45.0)] // 相等：死区为 0 也拒绝（镜像服务端 <=）
    [InlineData(-50.0, -45.0)] // 解锁阈值更低：负死区
    public void Validate_UnlockNotGreaterThanLock_Message(double unlock, double lockThreshold)
    {
        var config = Valid();
        config.Proximity.UnlockThresholdDbm = unlock;
        config.Proximity.LockThresholdDbm = lockThreshold;

        var error = Assert.Single(ConfigValidator.Validate(config).Errors);
        Assert.Equal("Proximity.UnlockThresholdDbm 必须大于 LockThresholdDbm，否则死区为负", error.Message);
    }

    [Fact]
    public void Validate_UnlockSampleCount_Boundaries()
    {
        var config = Valid();
        config.Proximity.UnlockSampleCount = 1;
        Assert.True(ConfigValidator.Validate(config).IsValid);
        config.Proximity.UnlockSampleCount = 20;
        Assert.True(ConfigValidator.Validate(config).IsValid); // 上限含 20

        config.Proximity.UnlockSampleCount = 21;
        Assert.Equal("Proximity.UnlockSampleCount 必须在 1 到 20 之间",
            Assert.Single(ConfigValidator.Validate(config).Errors).Message);

        config.Proximity.UnlockSampleCount = 0;
        Assert.Equal("Proximity.UnlockSampleCount 必须在 1 到 20 之间",
            Assert.Single(ConfigValidator.Validate(config).Errors).Message);
    }

    [Fact]
    public void Validate_LockPromptSampleCount_Boundaries()
    {
        var config = Valid();
        config.Proximity.LockPromptSampleCount = 1;
        Assert.True(ConfigValidator.Validate(config).IsValid);
        config.Proximity.LockPromptSampleCount = 50;
        Assert.True(ConfigValidator.Validate(config).IsValid); // 上限含 50

        config.Proximity.LockPromptSampleCount = 51;
        Assert.Equal("Proximity.LockPromptSampleCount 必须在 1 到 50 之间",
            Assert.Single(ConfigValidator.Validate(config).Errors).Message);
    }

    [Fact]
    public void Validate_LockConfirmSampleCount_Boundaries()
    {
        var config = Valid();
        config.Proximity.LockConfirmSampleCount = 1;
        Assert.True(ConfigValidator.Validate(config).IsValid);
        config.Proximity.LockConfirmSampleCount = 50;
        Assert.True(ConfigValidator.Validate(config).IsValid); // 上限含 50

        config.Proximity.LockConfirmSampleCount = 0;
        Assert.Equal("Proximity.LockConfirmSampleCount 必须在 1 到 50 之间",
            Assert.Single(ConfigValidator.Validate(config).Errors).Message);
    }

    [Fact]
    public void Validate_SampleIntervalMs_Boundaries()
    {
        var config = Valid();
        config.Proximity.SampleIntervalMs = 0;
        Assert.True(ConfigValidator.Validate(config).IsValid);
        config.Proximity.SampleIntervalMs = 2000;
        Assert.True(ConfigValidator.Validate(config).IsValid);
        config.Proximity.SampleIntervalMs = 100;
        Assert.True(ConfigValidator.Validate(config).IsValid);

        config.Proximity.SampleIntervalMs = 99;
        Assert.Equal("Proximity.SampleIntervalMs 必须是 0（每条广播都计入）或 100 到 2000 之间",
            Assert.Single(ConfigValidator.Validate(config).Errors).Message);

        config.Proximity.SampleIntervalMs = -1;
        Assert.Equal("Proximity.SampleIntervalMs 必须是 0（每条广播都计入）或 100 到 2000 之间",
            Assert.Single(ConfigValidator.Validate(config).Errors).Message);

        config.Proximity.SampleIntervalMs = 2001;
        Assert.Equal("Proximity.SampleIntervalMs 必须是 0（每条广播都计入）或 100 到 2000 之间",
            Assert.Single(ConfigValidator.Validate(config).Errors).Message);
    }

    [Fact]
    public void Validate_StartupGrace_Boundaries()
    {
        var config = Valid();
        config.Proximity.StartupGraceSeconds = 0;
        Assert.True(ConfigValidator.Validate(config).IsValid); // 0 合法

        config.Proximity.StartupGraceSeconds = -0.1;
        Assert.Equal("Proximity.StartupGraceSeconds 不能为负数",
            Assert.Single(ConfigValidator.Validate(config).Errors).Message);
    }

    [Fact]
    public void Validate_UnlockPendingTimeout_Message()
    {
        var config = Valid();
        config.Proximity.UnlockPendingTimeoutSeconds = 0;

        Assert.Equal("Proximity.UnlockPendingTimeoutSeconds 必须为正数",
            Assert.Single(ConfigValidator.Validate(config).Errors).Message);
    }

    [Fact]
    public void Validate_SnoozeMinutes_Zero_Message()
    {
        var config = Valid();
        config.Notification.SnoozeMinutes = 0;

        Assert.Equal("Notification.SnoozeMinutes 必须为正数",
            Assert.Single(ConfigValidator.Validate(config).Errors).Message);
    }

    [Fact]
    public void Validate_SnoozeMinutes_UpperBoundary()
    {
        var config = Valid();
        config.Notification.SnoozeMinutes = 240;
        Assert.True(ConfigValidator.Validate(config).IsValid);

        config.Notification.SnoozeMinutes = 240.5;
        Assert.Equal("Notification.SnoozeMinutes 不能超过 240 分钟",
            Assert.Single(ConfigValidator.Validate(config).Errors).Message);
    }

    [Fact]
    public void Validate_RejectSnooze_Message()
    {
        var config = Valid();
        config.Notification.RejectSnoozeSeconds = 0;

        Assert.Equal("Notification.RejectSnoozeSeconds 必须为正数",
            Assert.Single(ConfigValidator.Validate(config).Errors).Message);
    }

    [Fact]
    public void Validate_MultipleErrors_FollowServerOrder()
    {
        var config = Valid();
        config.Device.Irk = "bad";
        config.Credentials.UserName = "a\nb";
        config.Proximity.LockConfirmSampleCount = 0;
        config.Notification.SnoozeMinutes = 999;

        var errors = ConfigValidator.Validate(config).Errors;
        Assert.Equal(4, errors.Count);
        Assert.Equal(ConfigFields.DeviceIrk, errors[0].Field);
        Assert.Equal(ConfigFields.CredentialsUserName, errors[1].Field);
        Assert.Equal(ConfigFields.ProximityLockConfirmSampleCount, errors[2].Field);
        Assert.Equal(ConfigFields.NotificationSnoozeMinutes, errors[3].Field);
    }
}

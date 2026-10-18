using AutoUnlockManager.Core.Config;
using AutoUnlockService.Config;
using Xunit;

namespace AutoUnlockManager.Tests;

/// <summary>差异计算测试：归一化判定、展示格式化、敏感字段与顺序。</summary>
public sealed class ConfigDiffTests
{
    private static AutoUnlockConfig Baseline()
    {
        var config = new AutoUnlockConfig();
        config.Device.Irk = "00112233445566778899AABBCCDDEEFF";
        config.Device.IdentityAddress = "aa-bb-cc-dd-ee-ff";
        config.Credentials.UserName = "user1";
        config.Credentials.Password = "pw1";
        return config;
    }

    [Fact]
    public void Compute_Identical_Empty()
        => Assert.Empty(ConfigDiff.Compute(Baseline(), Baseline()));

    [Fact]
    public void Compute_IrkFormattingOnly_NotAChange()
    {
        var from = Baseline();
        var to = Baseline();
        to.Device.Irk = "00:11:22:33:44:55:66:77:88:99:aa:bb:cc:dd:ee:ff";
        to.Device.IdentityAddress = "AABBCCDDEEFF";

        Assert.Empty(ConfigDiff.Compute(from, to));
    }

    [Fact]
    public void Compute_IrkChanged_TruncatedDisplay()
    {
        var from = Baseline();
        var to = Baseline();
        to.Device.Irk = "AA112233445566778899AABBCCDDEEFF";

        var change = Assert.Single(ConfigDiff.Compute(from, to));
        Assert.Equal(ConfigFields.DeviceIrk, change.Field);
        Assert.Equal("设备 IRK", change.DisplayName);
        Assert.Equal("00112233…eeff", change.OldText);
        Assert.Equal("aa112233…eeff", change.NewText);
        Assert.False(change.IsSensitive);
    }

    [Fact]
    public void Compute_PasswordChanged_SensitiveWithoutValues()
    {
        var from = Baseline();
        var to = Baseline();
        to.Credentials.Password = "pw2";

        var change = Assert.Single(ConfigDiff.Compute(from, to));
        Assert.Equal(ConfigFields.CredentialsPassword, change.Field);
        Assert.Equal("凭据密码", change.DisplayName);
        Assert.True(change.IsSensitive);
        Assert.Null(change.OldText);
        Assert.Null(change.NewText);
    }

    [Fact]
    public void Compute_UserNameChanged_ShowsValues()
    {
        var from = Baseline();
        var to = Baseline();
        to.Credentials.UserName = "user2";

        var change = Assert.Single(ConfigDiff.Compute(from, to));
        Assert.Equal("凭据用户名", change.DisplayName);
        Assert.Equal("user1", change.OldText);
        Assert.Equal("user2", change.NewText);
    }

    [Fact]
    public void Compute_AddressCleared_ShowsUnset()
    {
        var from = Baseline();
        var to = Baseline();
        to.Device.IdentityAddress = null;

        var change = Assert.Single(ConfigDiff.Compute(from, to));
        Assert.Equal("设备地址", change.DisplayName);
        Assert.Equal("aabbccddeeff", change.OldText);
        Assert.Equal("（未设置）", change.NewText);
    }

    [Fact]
    public void Compute_Numbers_InvariantFormat()
    {
        var from = Baseline();
        var to = Baseline();
        to.Proximity.LockThresholdDbm = -75.5;
        to.Proximity.UnlockSampleCount = 4;

        var changes = ConfigDiff.Compute(from, to);
        Assert.Equal(2, changes.Count);
        Assert.Equal("锁定阈值（dBm）", changes[0].DisplayName);
        Assert.Equal("-80", changes[0].OldText);
        Assert.Equal("-75.5", changes[0].NewText);
        Assert.Equal("解锁确认个数", changes[1].DisplayName);
        Assert.Equal("3", changes[1].OldText);
        Assert.Equal("4", changes[1].NewText);
    }

    [Fact]
    public void Compute_Enums_ChineseLabels()
    {
        var from = Baseline();
        var to = Baseline();
        to.Device.IrkByteOrder = IrkByteOrder.Reversed;
        to.Notification.Mode = NotifierMode.Dialog;

        var changes = ConfigDiff.Compute(from, to);
        Assert.Equal(2, changes.Count);
        Assert.Equal("IRK 字节序", changes[0].DisplayName);
        Assert.Equal("自动检测（Auto）", changes[0].OldText);
        Assert.Equal("反序（Reversed）", changes[0].NewText);
        Assert.Equal("通知方式", changes[1].DisplayName);
        Assert.Equal("系统通知（Toast）", changes[1].OldText);
        Assert.Equal("对话框（Dialog）", changes[1].NewText);
    }

    [Fact]
    public void Compute_FieldOrder_FollowsForm()
    {
        var from = Baseline();
        var to = Baseline();
        to.Notification.SnoozeMinutes = 10;
        to.Credentials.UserName = "user2";
        to.Proximity.LockConfirmSampleCount = 11;
        to.Proximity.UnlockPendingTimeoutSeconds = 30;

        var changes = ConfigDiff.Compute(from, to);
        Assert.Equal(
            [ConfigFields.CredentialsUserName, ConfigFields.ProximityLockConfirmSampleCount,
             ConfigFields.ProximityUnlockPendingTimeoutSeconds, ConfigFields.NotificationSnoozeMinutes],
            changes.Select(c => c.Field));
    }

    [Fact]
    public void Compute_AllFifteenFields_Covered()
    {
        var from = new AutoUnlockConfig();
        from.Device.Irk = "00112233445566778899aabbccddeeff";
        from.Credentials.UserName = "u";
        from.Credentials.Password = "p";

        var to = from.Clone();
        to.Device.Irk = "ff112233445566778899aabbccddeeff";
        to.Device.IrkByteOrder = IrkByteOrder.Normal;
        to.Device.IdentityAddress = "AABBCCDDEEFF";
        to.Credentials.UserName = "u2";
        to.Credentials.Password = "p2";
        to.Proximity.LockThresholdDbm = -81;
        to.Proximity.UnlockThresholdDbm = -46;
        to.Proximity.UnlockSampleCount = 4;
        to.Proximity.LockPromptSampleCount = 6;
        to.Proximity.LockConfirmSampleCount = 11;
        to.Proximity.StartupGraceSeconds = 16;
        to.Proximity.UnlockPendingTimeoutSeconds = 21;
        to.Notification.Mode = NotifierMode.Dialog;
        to.Notification.RejectSnoozeSeconds = 61;
        to.Notification.SnoozeMinutes = 6;

        var changes = ConfigDiff.Compute(from, to);
        Assert.Equal(15, changes.Count);
        Assert.Equal(
            [
                ConfigFields.DeviceIrk, ConfigFields.DeviceIrkByteOrder, ConfigFields.DeviceIdentityAddress,
                ConfigFields.CredentialsUserName, ConfigFields.CredentialsPassword,
                ConfigFields.ProximityLockThresholdDbm, ConfigFields.ProximityUnlockThresholdDbm,
                ConfigFields.ProximityUnlockSampleCount, ConfigFields.ProximityLockPromptSampleCount,
                ConfigFields.ProximityLockConfirmSampleCount, ConfigFields.ProximityStartupGraceSeconds,
                ConfigFields.ProximityUnlockPendingTimeoutSeconds,
                ConfigFields.NotificationMode, ConfigFields.NotificationRejectSnoozeSeconds,
                ConfigFields.NotificationSnoozeMinutes,
            ],
            changes.Select(c => c.Field));
    }

    [Fact]
    public void Compute_NullStrings_ToleratedNoThrow()
    {
        // 评审 I1 防御：极端输入（null 字符串）不得抛异常，展示为「（空）」
        var from = new AutoUnlockConfig();
        from.Device.Irk = null!;
        from.Credentials.UserName = null!;
        var to = from.Clone();
        to.Credentials.UserName = "u";

        var change = Assert.Single(ConfigDiff.Compute(from, to));
        Assert.Equal(ConfigFields.CredentialsUserName, change.Field);
        Assert.Equal("（空）", change.OldText);
        Assert.Equal("u", change.NewText);
    }

    [Fact]
    public void Compute_ProtectedIrk_Changed_ShowsDecryptedValues()
    {
        var from = Baseline();
        from.Device.Irk = SecretProtector.Protect("00112233445566778899aabbccddeeff");
        var to = Baseline();
        to.Device.Irk = "FFEEBBAA99887766554433221100FFEE";

        var change = Assert.Single(ConfigDiff.Compute(from, to));

        Assert.Equal(ConfigFields.DeviceIrk, change.Field);
        Assert.Equal("00112233…eeff", change.OldText);  // 密文解密后截断显示
        Assert.Equal("ffeebbaa…ffee", change.NewText);
        Assert.False(change.IsSensitive);
    }

    [Fact]
    public void Compute_ProtectedIrk_SameValueAsPlain_NoChange()
    {
        // 磁盘密文 vs 表单回显的同值明文：不算变更（否则每次保存都误报 IRK/密码已改）
        var from = Baseline();
        from.Device.Irk = SecretProtector.Protect("00112233445566778899aabbccddeeff");
        from.Credentials.Password = SecretProtector.Protect("pw1");
        var to = Baseline();

        Assert.Empty(ConfigDiff.Compute(from, to));
    }

    [Fact]
    public void Compute_UnreadableCipher_OldShowsEncryptedPlaceholder()
    {
        var from = Baseline();
        from.Device.Irk = SecretTestData.Tampered(SecretProtector.Protect("00112233445566778899aabbccddeeff"));
        var to = Baseline();

        var change = Assert.Single(ConfigDiff.Compute(from, to));

        Assert.Equal(ConfigFields.DeviceIrk, change.Field);
        Assert.Equal("（已加密）", change.OldText);      // 解不开：不展开
        Assert.Equal("00112233…eeff", change.NewText);
    }

    [Fact]
    public void Compute_IdenticalProtectedValues_NoChange()
    {
        var from = Baseline();
        from.Device.Irk = SecretProtector.Protect("00112233445566778899aabbccddeeff");
        from.Credentials.Password = SecretProtector.Protect("pw1");
        var to = from.Clone(); // 密文原样（未编辑）

        Assert.Empty(ConfigDiff.Compute(from, to));
    }

    [Fact]
    public void Compute_ProtectedPassword_Changed_SensitiveRow()
    {
        var from = Baseline();
        from.Credentials.Password = SecretProtector.Protect("pw1");
        var to = Baseline();
        to.Credentials.Password = "pw2";

        var change = Assert.Single(ConfigDiff.Compute(from, to));
        Assert.Equal(ConfigFields.CredentialsPassword, change.Field);
        Assert.True(change.IsSensitive);
    }
}

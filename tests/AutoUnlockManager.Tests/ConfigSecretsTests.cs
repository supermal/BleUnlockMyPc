using AutoUnlockManager.Core.Config;
using AutoUnlockService.Config;
using Xunit;

namespace AutoUnlockManager.Tests;

/// <summary>敏感字段管理端约定测试：「留空保持不变」、保存前加密、字段提示与不可解密判定。</summary>
public sealed class ConfigSecretsTests
{
    [Fact]
    public void DisplayText_HidesProtected_ShowsPlain()
    {
        Assert.Equal("", ConfigSecrets.DisplayText(SecretProtector.Protect("x")));
        Assert.Equal("", ConfigSecrets.DisplayText(""));
        Assert.Equal("plain", ConfigSecrets.DisplayText("plain"));
    }

    [Fact]
    public void ResolveInput_EmptyKeepsProtected_OtherwiseUsesText()
    {
        var cipher = SecretProtector.Protect("x");

        Assert.Equal(cipher, ConfigSecrets.ResolveInput("", cipher));       // 留空 = 保留密文
        Assert.Equal(cipher, ConfigSecrets.ResolveInput(null, cipher));
        Assert.Equal("nuevo", ConfigSecrets.ResolveInput("nuevo", cipher)); // 输入 = 替换
        Assert.Equal("", ConfigSecrets.ResolveInput("", "plain"));          // 明文被清空 = 空
        Assert.Equal("", ConfigSecrets.ResolveInput("", ""));
    }

    [Fact]
    public void ProtectNewValues_EncryptsPlain_KeepsCipher_KeepsEmpty()
    {
        var cipher = SecretProtector.Protect("viejo");
        var config = new AutoUnlockConfig();
        config.Device.Irk = "00112233445566778899AABBCCDDEEFF";
        config.Credentials.Password = cipher;

        Assert.True(ConfigSecrets.ProtectNewValues(config));
        Assert.True(SecretProtector.IsProtected(config.Device.Irk));
        Assert.True(SecretProtector.TryUnprotect(config.Device.Irk, out var irk));
        Assert.Equal("00112233445566778899AABBCCDDEEFF", irk); // 原样加密（归一化由调用方先行）
        Assert.Equal(cipher, config.Credentials.Password);      // 密文原样保留
        Assert.False(ConfigSecrets.ProtectNewValues(config));   // 二次调用无变化

        var empty = new AutoUnlockConfig();
        Assert.False(ConfigSecrets.ProtectNewValues(empty));    // 空值跳过
        Assert.Equal("", empty.Device.Irk);
        Assert.Equal("", empty.Credentials.Password);
    }

    [Fact]
    public void HintText_OnlyForProtected_AndWarnsWhenUnreadable()
    {
        Assert.Equal("", ConfigSecrets.HintText("plain"));
        Assert.Equal("", ConfigSecrets.HintText(""));
        Assert.Contains("留空保持不变", ConfigSecrets.HintText(SecretProtector.Protect("x")));
        Assert.Contains("无法在本机解密",
            ConfigSecrets.HintText(SecretTestData.Tampered(SecretProtector.Protect("x"))));
    }

    [Fact]
    public void IsUnreadableOnThisMachine_Rules()
    {
        Assert.False(ConfigSecrets.IsUnreadableOnThisMachine(""));
        Assert.False(ConfigSecrets.IsUnreadableOnThisMachine("plain"));
        Assert.False(ConfigSecrets.IsUnreadableOnThisMachine(SecretProtector.Protect("x")));
        Assert.True(ConfigSecrets.IsUnreadableOnThisMachine(SecretTestData.Tampered(SecretProtector.Protect("x"))));
    }
}

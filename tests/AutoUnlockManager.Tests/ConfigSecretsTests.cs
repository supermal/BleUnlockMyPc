using AutoUnlockManager.Core.Config;
using AutoUnlockService.Config;
using Xunit;

namespace AutoUnlockManager.Tests;

/// <summary>敏感字段管理端约定测试：解密回显、保存前加密、字段提示、不可解密判定与差异比较取值。</summary>
public sealed class ConfigSecretsTests
{
    [Fact]
    public void DisplayText_DecryptsProtected_ShowsPlain()
    {
        var cipher = SecretProtector.Protect("valor");

        Assert.Equal("valor", ConfigSecrets.DisplayText(cipher));                     // 密文 → 明文回显
        Assert.Equal("", ConfigSecrets.DisplayText(SecretTestData.Tampered(cipher))); // 解不开 → 空（提示重新输入）
        Assert.Equal("", ConfigSecrets.DisplayText(""));
        Assert.Equal("plain", ConfigSecrets.DisplayText("plain"));
    }

    [Fact]
    public void DecryptForCompare_DecryptsOrReturnsRaw()
    {
        var cipher = SecretProtector.Protect("valor");
        var tampered = SecretTestData.Tampered(cipher);

        Assert.Equal("valor", ConfigSecrets.DecryptForCompare(cipher));
        Assert.Equal(tampered, ConfigSecrets.DecryptForCompare(tampered)); // 解不开原样（与任何明文都不等 → 视为变更）
        Assert.Equal("plain", ConfigSecrets.DecryptForCompare("plain"));
        Assert.Equal("", ConfigSecrets.DecryptForCompare(null));
        Assert.Equal("", ConfigSecrets.DecryptForCompare(""));
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
    public void HintText_PlainProtectedAndUnreadable()
    {
        Assert.Equal("", ConfigSecrets.HintText(""));
        Assert.Contains("以明文存储", ConfigSecrets.HintText("plain"));
        Assert.Contains("已加密存储", ConfigSecrets.HintText(SecretProtector.Protect("x")));
        Assert.DoesNotContain("眼睛", ConfigSecrets.HintText(SecretProtector.Protect("x")));
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

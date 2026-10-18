using AutoUnlockService.Config;
using Xunit;

namespace AutoUnlockService.Tests;

/// <summary>敏感值解析测试：密文就地解密、明文兼容告警、跨机器密文报错。</summary>
public class OptionsSecretsTests
{
    private const string Irk = "00112233445566778899aabbccddeeff";

    private static AutoUnlockOptions Options(string irk, string password) => new()
    {
        Device = new DeviceOptions { Irk = irk },
        Credentials = new CredentialOptions { UserName = "u", Password = password },
    };

    [Fact]
    public void Las_cadenas_vacias_no_producen_avisos_ni_errores()
    {
        var options = Options("", "");

        Assert.True(OptionsSecrets.TryResolve(options, out var warnings, out var errors));
        Assert.Empty(warnings);
        Assert.Empty(errors);
    }

    [Fact]
    public void El_texto_plano_produce_aviso_y_no_bloquea()
    {
        var options = Options(Irk, "pw");

        Assert.True(OptionsSecrets.TryResolve(options, out var warnings, out var errors));
        Assert.Empty(errors);
        Assert.Equal(2, warnings.Count);
        Assert.Contains("Device.Irk 以明文存储", warnings[0]);
        Assert.Contains("Credentials.Password 以明文存储", warnings[1]);
        Assert.Equal(Irk, options.Device.Irk); // 明文原样保留
        Assert.Equal("pw", options.Credentials.Password);
    }

    [Fact]
    public void El_cifrado_valido_se_sustituye_por_el_texto_plano()
    {
        var options = Options(SecretProtector.Protect(Irk), SecretProtector.Protect("pw"));

        Assert.True(OptionsSecrets.TryResolve(options, out var warnings, out var errors));
        Assert.Empty(warnings); // 已加密：不提示
        Assert.Empty(errors);
        Assert.Equal(Irk, options.Device.Irk);
        Assert.Equal("pw", options.Credentials.Password);
    }

    [Fact]
    public void El_cifrado_de_otra_maquina_es_un_error()
    {
        var options = Options(SecretTestData.Tampered(SecretProtector.Protect(Irk)), "pw");

        Assert.False(OptionsSecrets.TryResolve(options, out var warnings, out var errors));
        var error = Assert.Single(errors);
        Assert.Contains("Device.Irk 无法解密", error);
        Assert.Single(warnings); // 密码仍是明文：照常提示
        Assert.Contains("Credentials.Password 以明文存储", warnings[0]);
    }
}

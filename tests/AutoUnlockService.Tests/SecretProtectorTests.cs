using AutoUnlockService.Config;
using Xunit;

namespace AutoUnlockService.Tests;

/// <summary>dpapi:v1: 静态加密测试：往返、明文兼容、随机化、篡改不可解。</summary>
public class SecretProtectorTests
{
    [Fact]
    public void Proteger_y_descifrar_ida_y_vuelta()
    {
        const string plain = "P@ssw0rd·中文·'\"\\&";

        var cipher = SecretProtector.Protect(plain);

        Assert.StartsWith(SecretProtector.Prefix, cipher, StringComparison.Ordinal);
        Assert.True(SecretProtector.IsProtected(cipher));
        Assert.True(SecretProtector.TryUnprotect(cipher, out var roundtrip));
        Assert.Equal(plain, roundtrip);
    }

    [Fact]
    public void El_texto_plano_pasa_tal_cual_y_el_vacio_no_se_cifra()
    {
        Assert.True(SecretProtector.TryUnprotect("texto", out var plain));
        Assert.Equal("texto", plain);
        Assert.False(SecretProtector.IsProtected(""));
        Assert.False(SecretProtector.IsProtected(null));
        Assert.Equal("", SecretProtector.Protect(""));
    }

    [Fact]
    public void Dos_cifrados_del_mismo_valor_son_distintos_pero_descifrables()
    {
        var a = SecretProtector.Protect("secreto");
        var b = SecretProtector.Protect("secreto");

        Assert.NotEqual(a, b); // DPAPI 随机化：不会泄露「值未变」
        Assert.True(SecretProtector.TryUnprotect(a, out var pa));
        Assert.True(SecretProtector.TryUnprotect(b, out var pb));
        Assert.Equal("secreto", pa);
        Assert.Equal("secreto", pb);
    }

    [Fact]
    public void Un_cifrado_manipulado_no_se_descifra()
        => Assert.False(SecretProtector.TryUnprotect(SecretTestData.Tampered(SecretProtector.Protect("x")), out _));

    [Theory]
    [InlineData("dpapi:v1:")]     // 前缀后为空
    [InlineData("dpapi:v1:zzz!")] // Base64 非法
    [InlineData("dpapi:v1:AAAA")] // 合法 Base64 但不是 DPAPI blob
    public void Un_cifrado_invalido_no_se_descifra(string value)
        => Assert.False(SecretProtector.TryUnprotect(value, out _));
}

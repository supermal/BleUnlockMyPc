using AutoUnlockService.Config;
using Xunit;

namespace AutoUnlockManager.Tests;

/// <summary>共享加密实现（Link 编译自服务端）冒烟：往返、前缀、篡改不可解。</summary>
public sealed class SecretProtectorTests
{
    [Fact]
    public void Protect_RoundTrips_And_UsesPrefix()
    {
        var cipher = SecretProtector.Protect("pw-测试-123");

        Assert.StartsWith("dpapi:v1:", cipher, StringComparison.Ordinal);
        Assert.True(SecretProtector.TryUnprotect(cipher, out var plain));
        Assert.Equal("pw-测试-123", plain);
    }

    [Fact]
    public void PlainText_PassesThrough_TamperedFails()
    {
        Assert.True(SecretProtector.TryUnprotect("plain", out var plain));
        Assert.Equal("plain", plain);
        Assert.False(SecretProtector.TryUnprotect(SecretTestData.Tampered(SecretProtector.Protect("x")), out _));
    }
}

/// <summary>密文测试数据：篡改合法密文（翻转最后一个字节，破坏 DPAPI 完整性，必定无法解密）。</summary>
internal static class SecretTestData
{
    public static string Tampered(string cipher)
    {
        var blob = Convert.FromBase64String(cipher[SecretProtector.Prefix.Length..]);
        blob[^1] ^= 0xFF;
        return SecretProtector.Prefix + Convert.ToBase64String(blob);
    }
}

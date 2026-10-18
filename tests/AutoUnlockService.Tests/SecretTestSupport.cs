using AutoUnlockService.Config;
using Microsoft.Extensions.Logging;

namespace AutoUnlockService.Tests;

/// <summary>捕获日志文本的测试记录器（按格式化结果逐条记录）。</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Messages.Add(formatter(state, exception));
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

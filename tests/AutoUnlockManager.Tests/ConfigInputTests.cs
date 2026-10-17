using AutoUnlockManager.Core.Config;
using Xunit;

namespace AutoUnlockManager.Tests;

/// <summary>表单文本解析与格式化的测试（小数逗号容错、NaN/∞ 拒绝、Invariant 输出）。</summary>
public sealed class ConfigInputTests
{
    [Theory]
    [InlineData("-80", -80.0)]
    [InlineData(" -80.5 ", -80.5)]
    [InlineData("-80,5", -80.5)]     // es-ES 键盘逗号小数
    [InlineData("-80.5", -80.5)]
    [InlineData("1e3", 1000.0)]
    [InlineData("0", 0.0)]
    [InlineData("-0,25", -0.25)]
    public void TryParseNumber_Valid(string text, double expected)
    {
        Assert.True(ConfigInput.TryParseNumber(text, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("-80 dBm")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e999")]        // 溢出为 ∞：拒绝
    [InlineData("1.234,5")]      // 双分隔符歧义：拒绝
    [InlineData("1,234.5")]
    public void TryParseNumber_Invalid(string? text)
    {
        Assert.False(ConfigInput.TryParseNumber(text, out var value));
        Assert.Equal(0, value);
    }

    [Theory]
    [InlineData("3", 3)]
    [InlineData(" 5 ", 5)]
    [InlineData("0", 0)]
    [InlineData("-1", -1)] // 解析通过；范围由校验器负责
    public void TryParseCount_Valid(string text, int expected)
    {
        Assert.True(ConfigInput.TryParseCount(text, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("3.5")]   // 整数不能带小数
    [InlineData("3,5")]
    [InlineData("abc")]
    public void TryParseCount_Invalid(string? text)
        => Assert.False(ConfigInput.TryParseCount(text, out _));

    [Fact]
    public void Format_UsesInvariantCulture()
    {
        // 关键路径不依赖系统区域：始终小数点（es-ES 系统下也为 "10.5"）
        Assert.Equal("-80", ConfigInput.FormatNumber(-80));
        Assert.Equal("10.5", ConfigInput.FormatNumber(10.5));
        Assert.Equal("600.0001", ConfigInput.FormatNumber(600.0001));
        Assert.Equal("3", ConfigInput.FormatCount(3));
    }
}

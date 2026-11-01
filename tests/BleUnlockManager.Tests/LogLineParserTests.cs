using BleUnlockManager.Core.Logs;
using Xunit;

namespace BleUnlockManager.Tests;

/// <summary>日志行解析测试：头行/级别映射/续行继承/文化宽容/畸形行。</summary>
public sealed class LogLineParserTests
{
    [Fact]
    public void Header_Parsed_WithTimestampLevelAndMessage()
    {
        var entry = LogLineParser.Parse("2026-10-17 17:14:53.992 +02:00 [INF] 状态 \"PendingLock\" → \"Snoozed\"：用户延后 5 分钟");

        Assert.False(entry.IsContinuation);
        Assert.Equal(LogSeverity.Information, entry.Severity);
        Assert.Equal(
            new DateTimeOffset(2026, 10, 17, 17, 14, 53, 992, TimeSpan.FromHours(2)),
            entry.Timestamp);
        Assert.Equal("状态 \"PendingLock\" → \"Snoozed\"：用户延后 5 分钟", entry.Message);
        Assert.StartsWith("2026-10-17", entry.Raw);
    }

    [Theory]
    [InlineData("DBG", LogSeverity.Debug)]
    [InlineData("VRB", LogSeverity.Debug)]
    [InlineData("INF", LogSeverity.Information)]
    [InlineData("WRN", LogSeverity.Warning)]
    [InlineData("ERR", LogSeverity.Error)]
    [InlineData("FTL", LogSeverity.Fatal)]
    public void LevelCodes_Mapped(string code, LogSeverity expected)
        => Assert.Equal(expected, LogLineParser.ParseSeverity(code));

    [Fact]
    public void UnknownLevelCode_KeepsHeader_WithUnknownSeverity()
    {
        var entry = LogLineParser.Parse("2026-10-17 17:14:53.992 +02:00 [XYZ] 咦");

        Assert.False(entry.IsContinuation);
        Assert.Equal(LogSeverity.Unknown, entry.Severity);
        Assert.Equal("咦", entry.Message);
    }

    [Fact]
    public void MessageWithBrackets_And_CommaDecimals_Preserved()
    {
        var entry = LogLineParser.Parse("2026-10-17 17:14:53.992 +02:00 [WRN] 信号 [A1] 为 -60,5 dBm（es-ES 逗号）");

        Assert.Equal(LogSeverity.Warning, entry.Severity);
        Assert.Equal("信号 [A1] 为 -60,5 dBm（es-ES 逗号）", entry.Message);
    }

    [Fact]
    public void EmptyAndMalformedLines_AreContinuations()
    {
        foreach (var line in new[] { "", "   at Foo()", "2026-10-17 17:14:53 [INF] 缺毫秒与时区" })
        {
            var entry = LogLineParser.Parse(line);

            Assert.True(entry.IsContinuation);
            Assert.Equal(LogSeverity.Unknown, entry.Severity);
            Assert.Equal(line, entry.Message);
        }
    }

    [Fact]
    public void ParseBatch_InheritsSeverity_ForContinuations()
    {
        var lines = new[]
        {
            "2026-10-17 10:00:00.000 +02:00 [ERR] 出错：",
            "   at Foo()",
            "   内部异常: bar",
            "2026-10-17 10:00:01.000 +02:00 [INF] 恢复",
            "尾巴行",
        };

        var (entries, carry) = LogLineParser.ParseBatch(lines, LogSeverity.Unknown);

        Assert.Equal(5, entries.Count);
        Assert.Equal(LogSeverity.Error, entries[0].Severity);
        Assert.True(entries[1].IsContinuation);
        Assert.Equal(LogSeverity.Error, entries[1].Severity);
        Assert.Equal(LogSeverity.Error, entries[2].Severity);
        Assert.Equal(LogSeverity.Information, entries[3].Severity);
        Assert.True(entries[4].IsContinuation);
        Assert.Equal(LogSeverity.Information, entries[4].Severity);
        Assert.Equal(LogSeverity.Information, carry);
    }

    [Fact]
    public void ParseBatch_CarriesAcrossBatches_IsolatedHead_StaysUnknown()
    {
        var (first, carry1) = LogLineParser.ParseBatch(["孤立续行"], LogSeverity.Unknown);
        Assert.Equal(LogSeverity.Unknown, first[0].Severity);
        Assert.Equal(LogSeverity.Unknown, carry1);

        var (second, carry2) = LogLineParser.ParseBatch(
            ["2026-10-17 10:00:00.000 +02:00 [WRN] 警告"], carry1);
        Assert.Equal(LogSeverity.Warning, second[0].Severity);

        var (third, _) = LogLineParser.ParseBatch(["没时间戳的后续"], carry2);
        Assert.Equal(LogSeverity.Warning, third[0].Severity);
    }
}

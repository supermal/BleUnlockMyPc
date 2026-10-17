using AutoUnlockManager.Core.Logs;
using Xunit;

namespace AutoUnlockManager.Tests;

/// <summary>日志过滤测试：级别开关组合 × 关键字（大小写/空白）/ Unknown 行保留。</summary>
public sealed class LogFilterTests
{
    private static LogEntry E(LogSeverity severity, string message)
        => new($"ts [X] {message}", message, null, severity, IsContinuation: false);

    private static readonly List<LogEntry> All =
    [
        E(LogSeverity.Debug, "调试细节"),
        E(LogSeverity.Information, "普通事件 Alpha"),
        E(LogSeverity.Warning, "警告：信号弱"),
        E(LogSeverity.Error, "错误：连接失败"),
        E(LogSeverity.Fatal, "致命：崩溃"),
        E(LogSeverity.Unknown, "无法识别行"),
    ];

    [Fact]
    public void AllOn_NoKeyword_KeepsEverything()
        => Assert.Equal(6, LogFilter.Apply(All, new LogLevelFilter(), null).Count);

    [Fact]
    public void ErrorOnly_KeepsErrFatalAndUnknown()
    {
        var filter = new LogLevelFilter { Debug = false, Information = false, Warning = false };

        var result = LogFilter.Apply(All, filter, null);

        Assert.Equal(3, result.Count);
        Assert.Contains(result, e => e.Severity == LogSeverity.Error);
        Assert.Contains(result, e => e.Severity == LogSeverity.Fatal);
        Assert.Contains(result, e => e.Severity == LogSeverity.Unknown);
    }

    [Fact]
    public void Keyword_CaseInsensitive_AndTrimsWhitespace()
    {
        var result = LogFilter.Apply(All, new LogLevelFilter(), "  alpha ");

        Assert.Single(result);
        Assert.Equal("普通事件 Alpha", result[0].Message);
    }

    [Fact]
    public void BlankKeyword_IsNoFilter()
        => Assert.Equal(6, LogFilter.Apply(All, new LogLevelFilter(), "   ").Count);

    [Fact]
    public void AllOff_OnlyUnknownLinesRemain()
    {
        var filter = new LogLevelFilter { Debug = false, Information = false, Warning = false, Error = false };

        var result = LogFilter.Apply(All, filter, null);

        Assert.Single(result);
        Assert.Equal(LogSeverity.Unknown, result[0].Severity);
    }

    [Fact]
    public void OrderIsPreserved()
    {
        var result = LogFilter.Apply(All, new LogLevelFilter(), null);

        Assert.Equal(All.Select(e => e.Message), result.Select(e => e.Message));
    }
}

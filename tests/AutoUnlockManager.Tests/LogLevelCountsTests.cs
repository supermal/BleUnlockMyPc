using AutoUnlockManager.Core.Logs;
using Xunit;

namespace AutoUnlockManager.Tests;

public sealed class LogLevelCountsTests
{
    private static LogEntry Entry(LogSeverity severity, bool continuation = false)
        => new("raw", "msg", continuation ? null : DateTimeOffset.UnixEpoch, severity, continuation);

    [Fact]
    public void From_CountsEachLevel()
    {
        var counts = LogLevelCounts.From(
        [
            Entry(LogSeverity.Debug),
            Entry(LogSeverity.Information),
            Entry(LogSeverity.Information),
            Entry(LogSeverity.Warning),
            Entry(LogSeverity.Error),
            Entry(LogSeverity.Fatal),
        ]);

        Assert.Equal(new LogLevelCounts(1, 2, 1, 2), counts);
    }

    [Fact]
    public void From_IgnoresContinuationAndUnknownLines()
    {
        var counts = LogLevelCounts.From(
        [
            Entry(LogSeverity.Error),
            Entry(LogSeverity.Error, continuation: true),
            Entry(LogSeverity.Unknown),
        ]);

        Assert.Equal(new LogLevelCounts(0, 0, 0, 1), counts);
    }

    [Fact]
    public void Add_IncrementsSingleLevel()
        => Assert.Equal(new LogLevelCounts(0, 0, 1, 0), new LogLevelCounts(0, 0, 0, 0).Add(Entry(LogSeverity.Warning)));
}

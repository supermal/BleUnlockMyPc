namespace AutoUnlockManager.Core.Logs;

/// <summary>级别过滤集合（四开关；FTL 归 ERR 桶；Unknown 行始终保留，避免未识别内容凭空消失）。</summary>
public sealed record LogLevelFilter
{
    public bool Debug { get; init; } = true;

    public bool Information { get; init; } = true;

    public bool Warning { get; init; } = true;

    public bool Error { get; init; } = true;

    public bool Allows(LogSeverity severity) => severity switch
    {
        LogSeverity.Debug => Debug,
        LogSeverity.Information => Information,
        LogSeverity.Warning => Warning,
        LogSeverity.Error or LogSeverity.Fatal => Error,
        _ => true,
    };
}

/// <summary>日志过滤（设计 §5.3）：级别集合 + 关键字（OrdinalIgnoreCase 匹配整行原文；空白关键字视为无）。</summary>
public static class LogFilter
{
    /// <summary>单条是否通过过滤（增量追加路径复用；关键字空白视为无）。</summary>
    public static bool Matches(LogEntry entry, LogLevelFilter levels, string? keyword)
    {
        if (!levels.Allows(entry.Severity))
        {
            return false;
        }

        var normalized = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim();
        return normalized is null ||
               entry.Raw.Contains(normalized, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>按级别与关键字过滤，保持输入顺序。</summary>
    public static List<LogEntry> Apply(IReadOnlyList<LogEntry> entries, LogLevelFilter levels, string? keyword)
    {
        var result = new List<LogEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (Matches(entry, levels, keyword))
            {
                result.Add(entry);
            }
        }

        return result;
    }
}

using System.Globalization;

namespace AutoUnlockManager.Core.Logs;

/// <summary>日志级别（映射 Serilog 三字码；无法识别为 Unknown）。</summary>
public enum LogSeverity
{
    /// <summary>无法识别（畸形行、孤立续行、未知级别码）。</summary>
    Unknown,

    Debug,
    Information,
    Warning,
    Error,
    Fatal,
}

/// <summary>
/// 一行日志（物理行；不合并异常块——导出/过滤/计数与原文一一对应）。
/// <see cref="Raw"/> 为原文（不含换行）；<see cref="Message"/> 为级别码之后的正文；
/// 续行（无时间戳前缀，如异常堆栈）<see cref="IsContinuation"/> 为 true、消息即整行，
/// 级别由 <see cref="LogLineParser.ParseBatch"/> 按上一条头行继承。
/// </summary>
public sealed record LogEntry(
    string Raw,
    string Message,
    DateTimeOffset? Timestamp,
    LogSeverity Severity,
    bool IsContinuation)
{
    /// <summary>时间列文本（HH:mm:ss.fff；续行为空串）。</summary>
    public string TimeText => Timestamp is { } t
        ? t.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
        : "";

    /// <summary>级别列文本（三字码；未识别为 ???）。</summary>
    public string LevelText => Severity switch
    {
        LogSeverity.Debug => "DBG",
        LogSeverity.Information => "INF",
        LogSeverity.Warning => "WRN",
        LogSeverity.Error => "ERR",
        LogSeverity.Fatal => "FTL",
        _ => "???",
    };

    /// <summary>消息列文本（续行加两格缩进）。</summary>
    public string DisplayText => IsContinuation ? "  " + Message : Message;
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace BleUnlockManager.Core.Logs;

/// <summary>
/// 日志行解析（依据本机真实日志取证的 Serilog 默认文件模板，UTF-8）：
/// <c>yyyy-MM-dd HH:mm:ss.fff zzz [LVL] 消息</c>。带时间戳前缀的行视为头行，
/// 其余一律视为上一条的续行（异常堆栈等）。消息内容不做任何数字/文化解析（es-ES 逗号小数不受影响）。
/// </summary>
public static partial class LogLineParser
{
    [GeneratedRegex(
        @"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2})\s\[(?<lvl>[A-Z]{3})\]\s?(?<msg>.*)$")]
    private static partial Regex HeaderRegex();

    /// <summary>解析一行（不含换行）。无法匹配时间戳前缀时返回续行（级别 Unknown，待批量解析时继承）。</summary>
    public static LogEntry Parse(string line)
    {
        var match = HeaderRegex().Match(line);
        if (!match.Success)
        {
            return new LogEntry(line, line, null, LogSeverity.Unknown, IsContinuation: true);
        }

        DateTimeOffset? timestamp = DateTimeOffset.TryParseExact(
            match.Groups["ts"].Value,
            "yyyy-MM-dd HH:mm:ss.fff zzz",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed
            : null;

        return new LogEntry(
            line,
            match.Groups["msg"].Value,
            timestamp,
            ParseSeverity(match.Groups["lvl"].Value),
            IsContinuation: false);
    }

    /// <summary>
    /// 批量解析并解析续行级别继承：续行沿用「上一条头行」的级别；跨批次边界用
    /// <paramref name="carry"/> 传递（上一批次返回的最终级别）。返回解析后的条目与供下一批次续用的最终级别。
    /// </summary>
    public static (List<LogEntry> Entries, LogSeverity Carry) ParseBatch(
        IReadOnlyList<string> lines, LogSeverity carry)
    {
        var entries = new List<LogEntry>(lines.Count);
        foreach (var line in lines)
        {
            var entry = Parse(line);
            if (entry.IsContinuation)
            {
                entry = entry with { Severity = carry };
            }
            else
            {
                carry = entry.Severity;
            }

            entries.Add(entry);
        }

        return (entries, carry);
    }

    /// <summary>三字级别码 → 枚举（未知码为 Unknown）。</summary>
    public static LogSeverity ParseSeverity(string code) => code switch
    {
        "VRB" or "DBG" => LogSeverity.Debug,
        "INF" => LogSeverity.Information,
        "WRN" => LogSeverity.Warning,
        "ERR" => LogSeverity.Error,
        "FTL" => LogSeverity.Fatal,
        _ => LogSeverity.Unknown,
    };
}

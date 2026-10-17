using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoUnlockManager.Core.Config;

/// <summary>热重载结果分类。</summary>
public enum ConfigReloadStatus
{
    /// <summary>服务已应用新配置（日志出现「配置已重载」）。</summary>
    Reloaded,

    /// <summary>服务校验失败、保留了原配置（日志出现「配置校验失败，已保留原配置」）。</summary>
    Rejected,

    /// <summary>超时未确认（服务未运行/日志目录缺失/无对应日志行）。</summary>
    TimedOut,
}

/// <summary>热重载等待结果。<paramref name="Message"/> 为匹配到的日志行（去时间戳前缀），可空。</summary>
public sealed record ConfigReloadOutcome(
    ConfigReloadStatus Status,
    bool? DeviceChanged = null,
    bool? CredentialsChanged = null,
    string? Message = null);

/// <summary>保存前记录的日志检查点（当时最新文件与长度；文件缺失时 FilePath 为 null）。</summary>
public sealed record ConfigReloadCheckpoint(string? FilePath, long Length);

/// <summary>
/// 热重载确认（设计 §5.3）：保存前记录最新日志文件长度，保存后轮询尾部增量行，
/// 匹配服务端 <c>OptionsGate</c> 的「配置已重载（设备变更=…，凭据变更=…）」（成功）
/// 或「配置校验失败，已保留原配置：…」（拒绝）。行按字节缓冲、整行后再解码（容忍撕裂写与多字节字符）。
/// </summary>
public static partial class ConfigReloadWatcher
{
    /// <summary>轮询间隔。</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>成功标记（服务端日志原文）。</summary>
    public const string ReloadedMarker = "配置已重载";

    /// <summary>拒绝标记（服务端日志原文）。</summary>
    public const string RejectedMarker = "配置校验失败，已保留原配置";

    /// <summary>单次轮询读取上限（防日志暴涨时单次读入过大）。</summary>
    private const int MaxReadPerPoll = 1024 * 1024;

    /// <summary>未完成行缓冲上限（超过则丢弃最旧数据，标记行本身很短）。</summary>
    private const int MaxPendingBytes = 1024 * 1024;

    [GeneratedRegex(@"设备变更=(\w+)，凭据变更=(\w+)")]
    private static partial Regex ChangeFlagsRegex();

    /// <summary>
    /// 记录「保存前」的日志检查点（当时最新文件路径与长度）；目录/文件缺失时 FilePath 为 null、长度 0。
    /// </summary>
    public static ConfigReloadCheckpoint CaptureCheckpoint(string logsDir)
    {
        var newest = FindNewestLogFile(logsDir);
        return newest is null ? new ConfigReloadCheckpoint(null, 0) : new ConfigReloadCheckpoint(newest, SafeLength(newest));
    }

    /// <summary>
    /// 等待热重载结果：从检查点起增量读取日志（同一文件从检查点长度读；换日/换文件从 0 读），
    /// 命中标记行立即返回；超时返回 <see cref="ConfigReloadStatus.TimedOut"/>。
    /// </summary>
    public static async Task<ConfigReloadOutcome> WaitAsync(
        string logsDir, ConfigReloadCheckpoint checkpoint, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(logsDir))
        {
            return new ConfigReloadOutcome(ConfigReloadStatus.TimedOut, Message: "日志目录不存在");
        }

        var currentFile = FindNewestLogFile(logsDir);
        var position = currentFile is null
            ? 0
            : SameFile(currentFile, checkpoint.FilePath)
                ? SafeLength(currentFile) >= checkpoint.Length ? checkpoint.Length : 0 // 同文件被截断则从头读
                : 0; // 换文件（换日/重建）：从 0 读
        var pending = new List<byte>();

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var newest = FindNewestLogFile(logsDir);
            if (!string.Equals(newest, currentFile, StringComparison.OrdinalIgnoreCase))
            {
                currentFile = newest; // 换日/轮转：新文件从 0 读
                position = 0;
                pending.Clear();
            }

            if (currentFile is not null &&
                TryReadIncremental(currentFile, ref position, pending) is { } outcome)
            {
                return outcome;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        return new ConfigReloadOutcome(ConfigReloadStatus.TimedOut);
    }

    /// <summary>读取 <paramref name="file"/> 自 <paramref name="position"/> 起的增量字节，按行匹配标记；
    /// 瞬态 IO 失败（轮转/占用等）返回 null，由下一轮轮询重试（评审 I2：不得冒泡为“保存失败”）。</summary>
    private static ConfigReloadOutcome? TryReadIncremental(string file, ref long position, List<byte> pending)
    {
        try
        {
            return TryReadIncrementalCore(file, ref position, pending);
        }
        catch (IOException)
        {
            return null; // FileNotFound/DirectoryNotFound 等均为 IOException 子类
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ConfigReloadOutcome? TryReadIncrementalCore(string file, ref long position, List<byte> pending)
    {
        using var stream = new FileStream(
            file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        if (stream.Length < position)
        {
            position = 0; // 被截断重建
            pending.Clear();
        }

        var remaining = stream.Length - position;
        if (remaining <= 0)
        {
            return null; // 无新增字节；未完成尾部保留待下次续读
        }

        stream.Seek(position, SeekOrigin.Begin);
        var read = (int)Math.Min(remaining, MaxReadPerPoll);
        var buffer = new byte[read];
        var total = 0;
        while (total < read)
        {
            var n = stream.Read(buffer, total, read - total);
            if (n <= 0)
            {
                break;
            }

            total += n;
        }

        position += total;
        pending.AddRange(buffer.Take(total));

        if (pending.Count > MaxPendingBytes)
        {
            pending.RemoveRange(0, pending.Count - MaxPendingBytes);
        }

        // 扫描完整行（\n 结尾）
        var lineStart = 0;
        for (var i = 0; i < pending.Count; i++)
        {
            if (pending[i] != (byte)'\n')
            {
                continue;
            }

            var line = DecodeLine(pending, lineStart, i - lineStart);
            lineStart = i + 1;

            if (MatchLine(line) is { } outcome)
            {
                return outcome;
            }
        }

        pending.RemoveRange(0, lineStart); // 保留未完成尾部（下次轮询续读）
        return null;
    }

    private static string DecodeLine(List<byte> buffer, int offset, int count)
        => Encoding.UTF8.GetString(buffer.GetRange(offset, count).ToArray()).TrimEnd('\r');

    private static ConfigReloadOutcome? MatchLine(string line)
    {
        if (line.Contains(RejectedMarker, StringComparison.Ordinal))
        {
            var index = line.IndexOf(RejectedMarker, StringComparison.Ordinal);
            return new ConfigReloadOutcome(ConfigReloadStatus.Rejected, Message: line[index..].Trim());
        }

        if (line.Contains(ReloadedMarker, StringComparison.Ordinal))
        {
            bool? deviceChanged = null;
            bool? credentialsChanged = null;
            var match = ChangeFlagsRegex().Match(line);
            if (match.Success)
            {
                deviceChanged = ParseBoolean(match.Groups[1].Value);
                credentialsChanged = ParseBoolean(match.Groups[2].Value);
            }

            var index = line.IndexOf(ReloadedMarker, StringComparison.Ordinal);
            return new ConfigReloadOutcome(
                ConfigReloadStatus.Reloaded, deviceChanged, credentialsChanged, line[index..].Trim());
        }

        return null;
    }

    private static bool? ParseBoolean(string value) => value switch
    {
        "True" or "true" or "TRUE" => true,   // Serilog 布尔渲染随版本可能为 True/true
        "False" or "false" or "FALSE" => false,
        _ => null,
    };

    /// <summary>最新日志文件（文件名含日期戳，排序即时间序；目录缺失返回 null）。</summary>
    private static string? FindNewestLogFile(string logsDir)
    {
        try
        {
            var files = Directory.GetFiles(logsDir, AppPaths.LogFilePattern);
            if (files.Length == 0)
            {
                return null;
            }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            return files[^1];
        }
        catch
        {
            return null;
        }
    }

    private static bool SameFile(string? a, string? b)
        => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }
}

using System.Text;

namespace AutoUnlockManager.Core.Logs;

/// <summary>增量跟随结果：<paramref name="Lines"/> 为本次新增的完整行；<paramref name="FileMissing"/> 表示目标文件当前不可读。</summary>
public sealed record LogFollowResult(IReadOnlyList<string> Lines, bool FileMissing);

/// <summary>初始整文件读取结果：完整行序列 + 已消费字节数（最后一个完整换行之后的位置）。</summary>
public sealed record LogInitialRead(IReadOnlyList<string> Lines, long ConsumedBytes);

/// <summary>
/// 单文件增量读取器（设计 §5.3 日志页）：每次 <see cref="ReadNew"/> 重新打开文件
/// （FileShare.ReadWrite|Delete），按字节缓冲完整行后 UTF-8 解码；侦测截断重建（长度回退 → 从头读）；
/// 瞬态 IO 失败按「文件缺失」返回、位置不变，由调用方下一轮重试。不持有句柄，天然容忍共享写与轮转。
/// </summary>
public sealed class LogFollower
{
    /// <summary>单次读取上限（防日志暴涨时单次读入过大；未完成尾部保留续读）。</summary>
    private const int MaxReadPerPoll = 1024 * 1024;

    /// <summary>未完成字节缓冲上限（超出丢弃最旧数据）。</summary>
    private const int MaxPendingBytes = 1024 * 1024;

    private readonly List<byte> _pending = [];

    public LogFollower(string filePath, long startPosition = 0)
    {
        FilePath = filePath;
        Position = startPosition;
    }

    /// <summary>目标文件路径。</summary>
    public string FilePath { get; }

    /// <summary>已消费到的字节位置（完整行的边界之后）。</summary>
    public long Position { get; private set; }

    /// <summary>
    /// 读取自 <see cref="Position"/> 起的新增完整行。文件截断重建（长度回退）时从头读；
    /// 文件不存在/不可读时返回 FileMissing=true 且位置不变。
    /// </summary>
    public LogFollowResult ReadNew()
    {
        try
        {
            return ReadNewCore();
        }
        catch (IOException)
        {
            return new LogFollowResult([], FileMissing: true);
        }
        catch (UnauthorizedAccessException)
        {
            return new LogFollowResult([], FileMissing: true);
        }
    }

    /// <summary>
    /// 读取整个文件（初始加载）：尾部未完成的半行不计入行、也不计入
    /// <see cref="LogInitialRead.ConsumedBytes"/>——以此位置创建 follower 后，半行完成时不会丢行。
    /// 文件不可读时返回空结果（ConsumedBytes=0）。
    /// </summary>
    public static LogInitialRead ReadAll(string filePath)
    {
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            var lines = new List<string>();
            var lineStart = 0;
            long consumed = 0;
            for (var i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != (byte)'\n')
                {
                    continue;
                }

                lines.Add(Decode(bytes, lineStart, i - lineStart));
                lineStart = i + 1;
                consumed = lineStart;
            }

            return new LogInitialRead(lines, consumed);
        }
        catch (IOException)
        {
            return new LogInitialRead([], 0);
        }
        catch (UnauthorizedAccessException)
        {
            return new LogInitialRead([], 0);
        }
    }

    private LogFollowResult ReadNewCore()
    {
        using var stream = new FileStream(
            FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        if (stream.Length < Position)
        {
            Position = 0; // 截断重建
            _pending.Clear();
        }

        var remaining = stream.Length - Position;
        if (remaining > 0)
        {
            stream.Seek(Position, SeekOrigin.Begin);
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

            Position += total;
            _pending.AddRange(buffer.Take(total));
            if (_pending.Count > MaxPendingBytes)
            {
                _pending.RemoveRange(0, _pending.Count - MaxPendingBytes);
            }
        }

        return new LogFollowResult(ExtractCompleteLines(_pending), FileMissing: false);
    }

    private static List<string> ExtractCompleteLines(List<byte> pending)
    {
        var lines = new List<string>();
        var lineStart = 0;
        for (var i = 0; i < pending.Count; i++)
        {
            if (pending[i] != (byte)'\n')
            {
                continue;
            }

            lines.Add(Decode(pending, lineStart, i - lineStart));
            lineStart = i + 1;
        }

        if (lineStart > 0)
        {
            pending.RemoveRange(0, lineStart); // 保留未完成尾部（下次续读）
        }

        return lines;
    }

    private static string Decode(byte[] buffer, int offset, int count)
        => Encoding.UTF8.GetString(buffer, offset, count).TrimEnd('\r');

    private static string Decode(List<byte> buffer, int offset, int count)
        => Decode(buffer.GetRange(offset, count).ToArray(), 0, count);
}

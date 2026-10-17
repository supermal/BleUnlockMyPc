using System.Globalization;
using System.Text.RegularExpressions;

namespace AutoUnlockManager.Core.Logs;

/// <summary>日志文件清单项（<see cref="Date"/> 取自文件名日期戳，不可解析为 null）。</summary>
public sealed record LogFileInfo(string FullPath, string FileName, DateOnly? Date, long Length);

/// <summary>日志目录文件清单（设计 §5.3）：按文件名日期倒序（最新在前）；目录缺失/异常返回空列表。</summary>
public static partial class LogFiles
{
    [GeneratedRegex(@"^autounlock-(\d{8})\.log$", RegexOptions.IgnoreCase)]
    private static partial Regex FileNameRegex();

    public static IReadOnlyList<LogFileInfo> List(string logsDir)
    {
        try
        {
            var result = new List<LogFileInfo>();
            foreach (var path in Directory.GetFiles(logsDir, AppPaths.LogFilePattern))
            {
                var name = Path.GetFileName(path);
                var match = FileNameRegex().Match(name);
                DateOnly? date = null;
                if (match.Success &&
                    DateOnly.TryParseExact(
                        match.Groups[1].Value, "yyyyMMdd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var parsed))
                {
                    date = parsed;
                }

                long length = 0;
                try
                {
                    length = new FileInfo(path).Length;
                }
                catch
                {
                    // 瞬态（文件刚被轮转删除等）：长度按 0 处理
                }

                result.Add(new LogFileInfo(path, name, date, length));
            }

            result.Sort((a, b) =>
            {
                var byDate = Nullable.Compare(b.Date, a.Date); // 倒序（新在前）
                return byDate != 0 ? byDate : string.CompareOrdinal(b.FileName, a.FileName);
            });
            return result;
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}

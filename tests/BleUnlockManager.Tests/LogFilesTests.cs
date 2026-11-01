using BleUnlockManager.Core.Logs;
using Xunit;

namespace BleUnlockManager.Tests;

/// <summary>日志文件清单测试：倒序、日期解析、噪声忽略、缺失目录。</summary>
public sealed class LogFilesTests : IDisposable
{
    private readonly string _dir;

    public LogFilesTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aum-logfiles-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 清理失败不影响结论
        }
    }

    [Fact]
    public void List_SortsByDateDescending_ParsesDatesAndLengths()
    {
        File.WriteAllText(Path.Combine(_dir, "bleunlock-20261015.log"), "a");
        File.WriteAllText(Path.Combine(_dir, "bleunlock-20261017.log"), "xyz");
        File.WriteAllText(Path.Combine(_dir, "bleunlock-20261016.log"), "bb");
        File.WriteAllText(Path.Combine(_dir, "bleunlock-abc.log"), "x"); // 匹配目录模式但日期戳不可解析
        File.WriteAllText(Path.Combine(_dir, "其他.txt"), "noise");

        var files = LogFiles.List(_dir);

        Assert.Equal(4, files.Count);
        Assert.Equal("bleunlock-20261017.log", files[0].FileName);
        Assert.Equal("bleunlock-20261016.log", files[1].FileName);
        Assert.Equal("bleunlock-20261015.log", files[2].FileName);
        Assert.Equal("bleunlock-abc.log", files[3].FileName); // 无日期 → 排最后
        Assert.Equal(new DateOnly(2026, 10, 17), files[0].Date);
        Assert.Null(files[3].Date);
        Assert.Equal(3, files[0].Length);
    }

    [Fact]
    public void List_EmptyOrMissingDir_ReturnsEmpty()
    {
        Assert.Empty(LogFiles.List(_dir));
        Assert.Empty(LogFiles.List(Path.Combine(_dir, "没有这个目录")));
    }
}

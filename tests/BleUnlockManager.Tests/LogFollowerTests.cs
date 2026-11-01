using System.Text;
using BleUnlockManager.Core.Logs;
using Xunit;

namespace BleUnlockManager.Tests;

/// <summary>日志增量读取测试：整读半行、增量续读、UTF-8 跨界、截断重置、文件缺失。</summary>
public sealed class LogFollowerTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public LogFollowerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aum-logfollower-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "bleunlock-20261017.log");
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
    public void ReadAll_ReturnsCompleteLines_And_ConsumedPosition()
    {
        File.WriteAllText(_file, "行1\r\n行2\n尾部半行");

        var read = LogFollower.ReadAll(_file);

        Assert.Equal(2, read.Lines.Count);
        Assert.Equal("行1", read.Lines[0]);
        Assert.Equal("行2", read.Lines[1]);
        Assert.Equal(Encoding.UTF8.GetByteCount("行1\r\n行2\n"), read.ConsumedBytes);
    }

    [Fact]
    public void ReadAll_ThenFollower_CatchesCompletion_OfPartialTail()
    {
        File.WriteAllText(_file, "完成行\n半");
        var read = LogFollower.ReadAll(_file);
        Assert.Single(read.Lines);

        var follower = new LogFollower(_file, read.ConsumedBytes);
        File.AppendAllText(_file, "行完成\n");

        var follow = follower.ReadNew();

        Assert.False(follow.FileMissing);
        Assert.Single(follow.Lines);
        Assert.Equal("半行完成", follow.Lines[0]);
    }

    [Fact]
    public void ReadNew_Incremental_HandlesPartialLine()
    {
        File.WriteAllText(_file, "A\n");
        var follower = new LogFollower(_file);
        Assert.Equal(new[] { "A" }, follower.ReadNew().Lines);

        Assert.Empty(follower.ReadNew().Lines); // 无新增

        File.AppendAllText(_file, "B-半");
        Assert.Empty(follower.ReadNew().Lines); // 半行不返回

        File.AppendAllText(_file, "行\nC\n");
        Assert.Equal(new[] { "B-半行", "C" }, follower.ReadNew().Lines);
    }

    [Fact]
    public void ReadNew_Utf8Multibyte_SplitAcrossReads()
    {
        File.WriteAllText(_file, "");
        var follower = new LogFollower(_file);

        var bytes = Encoding.UTF8.GetBytes("中文\n");
        using (var fs = new FileStream(_file, FileMode.Append, FileAccess.Write))
        {
            fs.Write(bytes, 0, 1); // 只写首个字节（多字节字符被拆开）
        }

        Assert.Empty(follower.ReadNew().Lines);

        using (var fs = new FileStream(_file, FileMode.Append, FileAccess.Write))
        {
            fs.Write(bytes, 1, bytes.Length - 1);
        }

        Assert.Equal(new[] { "中文" }, follower.ReadNew().Lines);
    }

    [Fact]
    public void ReadNew_TruncatedFile_ResetsToStart()
    {
        File.WriteAllText(_file, "一\n二\n三\n");
        var follower = new LogFollower(_file);
        Assert.Equal(3, follower.ReadNew().Lines.Count);

        File.WriteAllText(_file, "新\n"); // 截断重建（长度回退）
        var again = follower.ReadNew();

        Assert.Equal(new[] { "新" }, again.Lines);
    }

    [Fact]
    public void ReadNew_MissingFile_ReportsMissing_AndKeepsPosition()
    {
        var follower = new LogFollower(Path.Combine(_dir, "不存在.log"));

        var result = follower.ReadNew();

        Assert.True(result.FileMissing);
        Assert.Empty(result.Lines);
        Assert.Equal(0, follower.Position);
    }

    [Fact]
    public void ReadAll_MissingFile_ReturnsEmpty()
    {
        var read = LogFollower.ReadAll(Path.Combine(_dir, "没有这个目录", "x.log"));

        Assert.Empty(read.Lines);
        Assert.Equal(0, read.ConsumedBytes);
    }
}

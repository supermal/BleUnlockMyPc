using System.Diagnostics;
using System.Text;
using AutoUnlockManager.Core.Config;
using Xunit;

namespace AutoUnlockManager.Tests;

/// <summary>热重载日志监听测试：增量匹配、偏移忽略、半行续读、新文件与超时。</summary>
public sealed class ConfigReloadWatcherTests : IDisposable
{
    private readonly string _dir;
    private readonly string _logFile;

    public ConfigReloadWatcherTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aum-watcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _logFile = Path.Combine(_dir, "autounlock-20261017.log");
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

    private void Append(string text) => File.AppendAllText(_logFile, text);

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan GenerousTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ReloadedMarker_Detected_WithFlags()
    {
        File.WriteAllText(_logFile, "2026-10-17 11:00:00.000 +02:00 [INF] 旧日志\n");
        var checkpoint = ConfigReloadWatcher.CaptureCheckpoint(_dir);
        Append("2026-10-17 12:00:00.000 +02:00 [INF] 配置已重载（设备变更=True，凭据变更=False）\n");

        var outcome = await ConfigReloadWatcher.WaitAsync(_dir, checkpoint, GenerousTimeout);

        Assert.Equal(ConfigReloadStatus.Reloaded, outcome.Status);
        Assert.True(outcome.DeviceChanged);
        Assert.False(outcome.CredentialsChanged);
        Assert.StartsWith("配置已重载", outcome.Message);
    }

    [Theory]
    [InlineData("true", "false")]
    [InlineData("True", "False")]
    public async Task ReloadedMarker_LowercaseBooleans_Tolerated(string device, string credentials)
    {
        File.WriteAllText(_logFile, "x\n");
        var checkpoint = ConfigReloadWatcher.CaptureCheckpoint(_dir);
        Append($"配置已重载（设备变更={device}，凭据变更={credentials}）\r\n");

        var outcome = await ConfigReloadWatcher.WaitAsync(_dir, checkpoint, GenerousTimeout);

        Assert.Equal(ConfigReloadStatus.Reloaded, outcome.Status);
        Assert.Equal(bool.Parse(device), outcome.DeviceChanged);
        Assert.Equal(bool.Parse(credentials), outcome.CredentialsChanged);
    }

    [Fact]
    public async Task ReloadedMarker_UnparseableFlags_StillReloaded()
    {
        File.WriteAllText(_logFile, "x\n");
        var checkpoint = ConfigReloadWatcher.CaptureCheckpoint(_dir);
        Append("配置已重载（设备变更=未知，凭据变更=未知）\n");

        var outcome = await ConfigReloadWatcher.WaitAsync(_dir, checkpoint, GenerousTimeout);

        Assert.Equal(ConfigReloadStatus.Reloaded, outcome.Status);
        Assert.Null(outcome.DeviceChanged);
        Assert.Null(outcome.CredentialsChanged);
    }

    [Fact]
    public async Task RejectedMarker_Detected_WithErrorMessage()
    {
        File.WriteAllText(_logFile, "x\n");
        var checkpoint = ConfigReloadWatcher.CaptureCheckpoint(_dir);
        Append("2026-10-17 12:00:00.000 +02:00 [ERR] 配置校验失败，已保留原配置：Device.Irk 必须是 32 个十六进制字符\n");
        Append("2026-10-17 12:00:00.000 +02:00 [ERR] 配置校验失败，已保留原配置：第二条错误\n");

        var outcome = await ConfigReloadWatcher.WaitAsync(_dir, checkpoint, GenerousTimeout);

        Assert.Equal(ConfigReloadStatus.Rejected, outcome.Status);
        Assert.Equal("配置校验失败，已保留原配置：Device.Irk 必须是 32 个十六进制字符", outcome.Message); // 首条即返回
    }

    [Fact]
    public async Task LinesBeforeOffset_Ignored()
    {
        File.WriteAllText(_logFile, "配置已重载（设备变更=True，凭据变更=True）\n"); // 早于偏移
        var checkpoint = ConfigReloadWatcher.CaptureCheckpoint(_dir);

        var outcome = await ConfigReloadWatcher.WaitAsync(_dir, checkpoint, ShortTimeout);

        Assert.Equal(ConfigReloadStatus.TimedOut, outcome.Status);
    }

    [Fact]
    public async Task PartialLine_NotMatched_UntilCompleted()
    {
        File.WriteAllText(_logFile, "x\n");
        var checkpoint = ConfigReloadWatcher.CaptureCheckpoint(_dir);

        // 半行（无换行）→ 不应匹配
        await using (var stream = new FileStream(_logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.Write(Encoding.UTF8.GetBytes("2026 [INF] 配置已"));
        }

        var first = await ConfigReloadWatcher.WaitAsync(_dir, checkpoint, ShortTimeout);
        Assert.Equal(ConfigReloadStatus.TimedOut, first.Status);

        // 续写余下部分与换行 → 整行匹配
        Append("重载（设备变更=False，凭据变更=True）\n");
        var second = await ConfigReloadWatcher.WaitAsync(_dir, checkpoint, GenerousTimeout);
        Assert.Equal(ConfigReloadStatus.Reloaded, second.Status);
        Assert.False(second.DeviceChanged);
        Assert.True(second.CredentialsChanged);
    }

    [Fact]
    public async Task SplitMultibyteCharacter_NotCorrupted()
    {
        File.WriteAllText(_logFile, "x\n");
        var checkpoint = ConfigReloadWatcher.CaptureCheckpoint(_dir);

        // 在多字节字符（“置” = E7 BD AE）中间截断写入：前 4 字节 = 配(3B) + 置 的第 1 字节，
        // 验证字节级缓冲在跨轮询的字符中间截断处不破坏解码（续写必须按字节，不能经 UTF-8 字符串往返）
        var line = Encoding.UTF8.GetBytes("配置已重载（设备变更=True，凭据变更=True）\n");
        await using (var stream = new FileStream(_logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.Write(line[..4]);
        }

        var pending = ConfigReloadWatcher.WaitAsync(_dir, checkpoint, TimeSpan.FromMilliseconds(400));
        await using (var stream = new FileStream(_logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.Write(line[4..]);
        }

        var outcome = await pending;

        Assert.Equal(ConfigReloadStatus.Reloaded, outcome.Status);
    }

    [Fact]
    public async Task NewFileAppears_ReadFromStart()
    {
        var olderPath = Path.Combine(_dir, "autounlock-20261017.log");
        File.WriteAllText(olderPath, "旧文件\n");
        var checkpoint = ConfigReloadWatcher.CaptureCheckpoint(_dir);

        // 模拟换日：新日期文件出现并含标记行
        var newerPath = Path.Combine(_dir, "autounlock-20261018.log");
        File.WriteAllText(newerPath, "配置已重载（设备变更=True，凭据变更=True）\n");

        var outcome = await ConfigReloadWatcher.WaitAsync(_dir, checkpoint, GenerousTimeout);

        Assert.Equal(ConfigReloadStatus.Reloaded, outcome.Status);
    }

    [Fact]
    public async Task TruncatedFile_ReadFromStart()
    {
        File.WriteAllText(_logFile, new string('x', 300) + "\n"); // 比新内容更长
        var checkpoint = ConfigReloadWatcher.CaptureCheckpoint(_dir);

        File.WriteAllText(_logFile, "配置已重载（设备变更=True，凭据变更=False）\n"); // 同文件被截断/重建

        var outcome = await ConfigReloadWatcher.WaitAsync(_dir, checkpoint, GenerousTimeout);

        Assert.Equal(ConfigReloadStatus.Reloaded, outcome.Status);
    }

    [Fact]
    public async Task MissingDir_ImmediateTimeout()
    {
        var missing = Path.Combine(_dir, "no-logs");
        var stopwatch = Stopwatch.StartNew();

        var outcome = await ConfigReloadWatcher.WaitAsync(missing, new ConfigReloadCheckpoint(null, 0), GenerousTimeout);

        stopwatch.Stop();
        Assert.Equal(ConfigReloadStatus.TimedOut, outcome.Status);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), "目录缺失应立即返回，不应等待完整超时");
    }

    [Fact]
    public void CaptureCheckpoint_NoFile_NullPath()
    {
        var checkpoint = ConfigReloadWatcher.CaptureCheckpoint(_dir);

        Assert.Null(checkpoint.FilePath);
        Assert.Equal(0, checkpoint.Length);
    }

    [Fact]
    public void CaptureCheckpoint_WithFile_ReturnsPathAndLength()
    {
        File.WriteAllText(_logFile, "abc");

        var checkpoint = ConfigReloadWatcher.CaptureCheckpoint(_dir);

        Assert.Equal(_logFile, checkpoint.FilePath);
        Assert.Equal(3, checkpoint.Length);
    }

    [Fact]
    public async Task LogFileLockedDuringWait_NoThrow_PollsUntilReadable()
    {
        // 评审 I2：等待期间日志被独占占用（轮转/AV 瞬态）不得冒泡为异常；解除后仍应命中标记
        File.WriteAllText(_logFile, "旧行\n");
        var checkpoint = ConfigReloadWatcher.CaptureCheckpoint(_dir);

        var exclusive = new FileStream(_logFile, FileMode.Open, FileAccess.Read, FileShare.None);
        var pending = ConfigReloadWatcher.WaitAsync(_dir, checkpoint, GenerousTimeout);
        await Task.Delay(500); // 覆盖至少两轮轮询（每轮打开必失败）
        exclusive.Dispose();
        Append("配置已重载（设备变更=True，凭据变更=False）\n");

        var outcome = await pending;

        Assert.Equal(ConfigReloadStatus.Reloaded, outcome.Status);
        Assert.True(outcome.DeviceChanged);
    }
}

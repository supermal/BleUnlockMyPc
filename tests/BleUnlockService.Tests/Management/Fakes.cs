using System.IO.Pipes;
using System.Text;
using BleUnlockService.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BleUnlockService.Tests.Management;

// 与生产同构的 OptionsGate 工厂：FakeTimeProvider 上可直接推进防抖
internal static class TestGates
{
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    // 合法初始配置（与 OptionsGateTests 同款）：设备与凭据齐全
    public static BleUnlockOptions ValidOptions() => new()
    {
        Device = new DeviceOptions { Irk = "ec0234a357c8ad05341010a60a397d9b" },
        Credentials = new CredentialOptions { UserName = "Admin", Password = "p" }
    };

    public static OptionsGate New(FakeTimeProvider time, BleUnlockOptions? initial = null) =>
        new(initial ?? ValidOptions(), NullLogger<OptionsGate>.Instance, time, Debounce);
}

// 管理端测试客户端：只读连接，按行读取服务端推送的 JSON 帧
internal sealed class FakeManagementClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;

    private FakeManagementClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
    }

    public static async Task<FakeManagementClient> ConnectAsync(string pipeName)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.In, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10_000);
        return new FakeManagementClient(pipe);
    }

    public async Task<string> ReadLineAsync(CancellationToken ct)
    {
        var line = await _reader.ReadLineAsync(ct);
        return line ?? throw new EndOfStreamException("管理端连接已被服务端关闭");
    }

    public ValueTask DisposeAsync()
    {
        _pipe.Dispose();
        return ValueTask.CompletedTask;
    }
}

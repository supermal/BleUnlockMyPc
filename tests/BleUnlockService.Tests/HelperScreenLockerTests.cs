using BleUnlockService.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BleUnlockService.Tests;

public class HelperScreenLockerTests
{
    private sealed class FakeChannel : IHelperCommandChannel
    {
        public bool IsConnected { get; set; } = true;
        public bool SendResult { get; set; } = true;
        public bool AckResult { get; set; } = true;
        public List<string> Sent { get; } = [];

        public Task<bool> SendAsync(string line, CancellationToken ct)
        {
            Sent.Add(line);
            return Task.FromResult(SendResult);
        }

        public Task<bool> WaitForLockedAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(AckResult);
    }

    private sealed class FakeLocker : IScreenLocker
    {
        public int Calls { get; private set; }
        public bool Result { get; set; } = true;

        public Task<bool> LockAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private static HelperScreenLocker Make(FakeChannel channel, FakeLocker fallback) =>
        new(channel, fallback, NullLogger.Instance);

    [Fact]
    public async Task Con_confirmacion_del_ayudante_no_usa_el_respaldo()
    {
        var channel = new FakeChannel();
        var fallback = new FakeLocker();

        Assert.True(await Make(channel, fallback).LockAsync(CancellationToken.None));
        Assert.Equal(0, fallback.Calls);
        Assert.Equal([HelperProtocol.BuildLock()], channel.Sent);
    }

    [Fact]
    public async Task Sin_ayudante_conectado_bloquea_por_el_respaldo()
    {
        var channel = new FakeChannel { IsConnected = false };
        var fallback = new FakeLocker();

        Assert.True(await Make(channel, fallback).LockAsync(CancellationToken.None));
        Assert.Equal(1, fallback.Calls);
        Assert.Empty(channel.Sent);
    }

    [Fact]
    public async Task Si_el_envio_falla_bloquea_por_el_respaldo()
    {
        var fallback = new FakeLocker();
        Assert.True(await Make(new FakeChannel { SendResult = false }, fallback).LockAsync(CancellationToken.None));
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public async Task Si_el_ayudante_no_confirma_bloquea_por_el_respaldo()
    {
        var fallback = new FakeLocker();
        Assert.True(await Make(new FakeChannel { AckResult = false }, fallback).LockAsync(CancellationToken.None));
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public async Task Devuelve_el_resultado_del_respaldo_si_tambien_falla()
    {
        var fallback = new FakeLocker { Result = false };
        Assert.False(await Make(new FakeChannel { IsConnected = false }, fallback).LockAsync(CancellationToken.None));
    }
}

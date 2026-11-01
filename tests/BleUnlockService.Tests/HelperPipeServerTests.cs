using System.Collections.Concurrent;
using BleUnlockService.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BleUnlockService.Tests;

// Usa tuberias con nombre reales (nombre unico por prueba): ademas de la logica, comprueba
// que la DACL deja entrar al usuario interactivo, que es bajo el que corre esta prueba
public class HelperPipeServerTests : IDisposable
{
    private readonly TestGuard _guard = new();
    private CancellationToken Guard => _guard.Token;

    public void Dispose() => _guard.Dispose();

    private static HelperPipeServer NewServer(string name, TimeProvider? time = null)
    {
        var server = new HelperPipeServer(NullLogger<HelperPipeServer>.Instance, time, name);
        server.Start(CancellationToken.None);
        return server;
    }

    private static Task<T> FirstEvent<T>(Action<Action<T>> subscribe)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscribe(v => tcs.TrySetResult(v));
        return tcs.Task;
    }

    [Fact]
    public async Task Cliente_interactivo_puede_conectar_y_el_HELLO_dispara_Connected()
    {
        var name = TestSupport.UniquePipeName();
        await using var server = NewServer(name);
        var connected = FirstEvent<int>(a => server.Connected += a);

        await using var client = await FakeHelperClient.ConnectAsync(name, sessionId: 7);

        Assert.Equal(7, await connected.WaitAsync(Guard));
        Assert.True(server.IsConnected);
    }

    [Fact]
    public async Task RESPONSE_del_ayudante_dispara_ResponseReceived()
    {
        var name = TestSupport.UniquePipeName();
        await using var server = NewServer(name);
        var response = FirstEvent<(int, PromptOutcome)>(a => server.ResponseReceived += (i, o) => a((i, o)));

        await using var client = await FakeHelperClient.ConnectAsync(name);
        await client.SendAsync(HelperProtocol.BuildResponse(42, PromptOutcome.Snooze));

        Assert.Equal((42, PromptOutcome.Snooze), await response.WaitAsync(Guard));
    }

    [Fact]
    public async Task Al_cerrar_el_cliente_se_dispara_Disconnected_y_IsConnected_es_falso()
    {
        var name = TestSupport.UniquePipeName();
        await using var server = NewServer(name);
        var connected = FirstEvent<int>(a => server.Connected += a);
        var disconnected = FirstEvent<bool>(a => server.Disconnected += () => a(true));

        var client = await FakeHelperClient.ConnectAsync(name);
        await connected.WaitAsync(Guard);
        await client.DisposeAsync();

        await disconnected.WaitAsync(Guard);
        Assert.False(server.IsConnected);
    }

    [Fact]
    public async Task El_ayudante_puede_reconectar_tras_desconectarse()
    {
        var name = TestSupport.UniquePipeName();
        await using var server = NewServer(name);
        var sessions = new BlockingCollection<int>();
        server.Connected += s => sessions.Add(s);

        var first = await FakeHelperClient.ConnectAsync(name, 1);
        Assert.Equal(1, sessions.Take(Guard));
        await first.DisposeAsync();

        await using var second = await FakeHelperClient.ConnectAsync(name, 2);
        Assert.Equal(2, sessions.Take(Guard));
    }

    [Fact]
    public async Task SendAsync_sin_conexion_devuelve_falso()
    {
        await using var server = NewServer(TestSupport.UniquePipeName());
        Assert.False(await server.SendAsync("NOTIFY x\n", Guard));
    }

    [Fact]
    public async Task SendAsync_entrega_la_linea_al_cliente()
    {
        var name = TestSupport.UniquePipeName();
        await using var server = NewServer(name);
        var connected = FirstEvent<int>(a => server.Connected += a);
        await using var client = await FakeHelperClient.ConnectAsync(name);
        await connected.WaitAsync(Guard);

        Assert.True(await server.SendAsync(HelperProtocol.BuildNotify("hola"), Guard));

        var command = await client.ReadCommandAsync(Guard);
        Assert.Equal(new HelperCommand.Notify("hola"), command);
    }

    [Fact]
    public async Task LOCKED_del_cliente_completa_WaitForLockedAsync_con_verdadero()
    {
        var name = TestSupport.UniquePipeName();
        await using var server = NewServer(name);
        var connected = FirstEvent<int>(a => server.Connected += a);
        await using var client = await FakeHelperClient.ConnectAsync(name);
        await connected.WaitAsync(Guard);

        // El cliente de prueba solo confirma: no hay ningun ayudante real ni se bloquea nada
        Assert.True(await server.SendAsync(HelperProtocol.BuildLock(), Guard));
        Assert.IsType<HelperCommand.Lock>(await client.ReadCommandAsync(Guard));
        await client.SendAsync(HelperProtocol.BuildLocked());

        Assert.True(await server.WaitForLockedAsync(TimeSpan.FromSeconds(30), Guard));
    }

    [Fact]
    public async Task WaitForLockedAsync_sin_confirmacion_vence_con_falso()
    {
        var name = TestSupport.UniquePipeName();
        var time = new FakeTimeProvider();
        await using var server = NewServer(name, time);
        var connected = FirstEvent<int>(a => server.Connected += a);
        await using var client = await FakeHelperClient.ConnectAsync(name);
        await connected.WaitAsync(Guard);

        Assert.True(await server.SendAsync(HelperProtocol.BuildLock(), Guard));
        var wait = server.WaitForLockedAsync(TimeSpan.FromSeconds(5), Guard);

        await TestSupport.AdvanceUntilAsync(time, () => wait.IsCompleted);
        Assert.False(await wait);
    }

    [Fact]
    public async Task La_desconexion_rompe_WaitForLockedAsync_sin_esperar_al_plazo()
    {
        var name = TestSupport.UniquePipeName();
        await using var server = NewServer(name);
        var connected = FirstEvent<int>(a => server.Connected += a);
        var client = await FakeHelperClient.ConnectAsync(name);
        await connected.WaitAsync(Guard);

        Assert.True(await server.SendAsync(HelperProtocol.BuildLock(), Guard));
        var wait = server.WaitForLockedAsync(TimeSpan.FromMinutes(10), Guard);
        await client.DisposeAsync();

        Assert.False(await wait.WaitAsync(Guard));
    }

    [Fact]
    public async Task Mensaje_ilegible_no_rompe_la_conexion()
    {
        var name = TestSupport.UniquePipeName();
        await using var server = NewServer(name);
        var response = FirstEvent<(int, PromptOutcome)>(a => server.ResponseReceived += (i, o) => a((i, o)));
        await using var client = await FakeHelperClient.ConnectAsync(name);

        await client.SendAsync("BASURA 1 2 3\n");
        await client.SendAsync(HelperProtocol.BuildResponse(1, PromptOutcome.Reject));

        Assert.Equal((1, PromptOutcome.Reject), await response.WaitAsync(Guard));
    }
}

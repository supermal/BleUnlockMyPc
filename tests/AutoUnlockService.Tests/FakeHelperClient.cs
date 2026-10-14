using System.IO.Pipes;
using System.Text;
using AutoUnlockService.Session;
using Xunit;

namespace AutoUnlockService.Tests;

// Hace de ayudante: se conecta al pipe de pruebas, lee ordenes y responde como lo haria el real
internal sealed class FakeHelperClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;

    private FakeHelperClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    }

    public static async Task<FakeHelperClient> ConnectAsync(string pipeName, int sessionId = 1)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10_000);
        var client = new FakeHelperClient(pipe);
        await client.SendAsync(HelperProtocol.BuildHello(sessionId));
        return client;
    }

    public Task SendAsync(string line) => _writer.WriteAsync(line);

    public async Task<string> ReadLineAsync(CancellationToken ct)
    {
        var line = await _reader.ReadLineAsync(ct);
        return line ?? throw new EndOfStreamException("El servidor cerro el canal");
    }

    public async Task<HelperCommand> ReadCommandAsync(CancellationToken ct)
    {
        var line = await ReadLineAsync(ct);
        Assert.True(HelperProtocol.TryParseCommand(line, out var command), $"Comando invalido: {line}");
        return command;
    }

    public ValueTask DisposeAsync()
    {
        _pipe.Dispose();
        return ValueTask.CompletedTask;
    }
}

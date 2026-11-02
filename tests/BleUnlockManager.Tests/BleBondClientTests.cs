using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using BleUnlockManager.Core.Bonds;
using Xunit;

namespace BleUnlockManager.Tests;

/// <summary>
/// BleBondClient 真管道集成测试（随机管道名）：正常列表、服务错误、坏数据与超时。
/// </summary>
public sealed class BleBondClientTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private static string NewPipeName() => $"BleUnlockTest-{Guid.NewGuid():N}";

    /// <summary>起一个只处理一次请求的假服务端；收到请求后回写一行响应。</summary>
    private static Task RunFakeServerAsync(string pipeName, string responseLine, Action<string>? onRequest = null)
        => Task.Run(async () =>
        {
            await using var server = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync();

            using var reader = new StreamReader(
                server, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096, leaveOpen: true);
            // Sin using: Dispose vacía el búfer contra el tubo y, si el cliente ya cerró (caso normal
            // en los tests de respuesta ilegible), lanza «Pipe is broken» de forma intermitente.
            // AutoFlush ya ha enviado todo y el tubo lo cierra el servidor
            var writer = new StreamWriter(
                server, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true)
            {
                AutoFlush = true
            };

            var request = await reader.ReadLineAsync();
            onRequest?.Invoke(request ?? string.Empty);
            await writer.WriteLineAsync(responseLine);

            // 等客户端读完并断开，避免服务端先释放管道导致响应丢失
            await reader.ReadLineAsync();
        });

    [Fact]
    public async Task List_parsea_los_dispositivos_y_manda_la_peticion_correcta()
    {
        var pipeName = NewPipeName();
        string? requestLine = null;
        var serverTask = RunFakeServerAsync(
            pipeName,
            """{"t":"res","id":1,"ok":true,"devices":[{"addr":"55:A2:DC:A6:30:DB","name":"Apple Watch","irk":"00112233445566778899aabbccddeeff"}]}""",
            line => requestLine = line);

        var result = await new BleBondClient(pipeName).ListAsync(TimeSpan.FromSeconds(5));

        await serverTask.WaitAsync(TestTimeout);

        Assert.True(result.Success);
        Assert.Null(result.Error);
        var device = Assert.Single(result.Devices);
        Assert.Equal("55:A2:DC:A6:30:DB", device.Address);
        Assert.Equal("Apple Watch", device.Name);
        Assert.Equal("00112233445566778899AABBCCDDEEFF", device.Irk);
        Assert.Equal("Apple Watch（55:A2:DC:A6:30:DB）", device.DisplayName);

        using var request = JsonDocument.Parse(requestLine!);
        Assert.Equal("req", request.RootElement.GetProperty("t").GetString());
        Assert.Equal(1, request.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("list", request.RootElement.GetProperty("op").GetString());
    }

    [Fact]
    public async Task List_conserva_los_dispositivos_sin_irk_como_no_compatibles()
    {
        var pipeName = NewPipeName();
        var serverTask = RunFakeServerAsync(
            pipeName,
            """{"t":"res","id":1,"ok":true,"devices":[{"addr":"11:22:33:44:55:66","name":"Pulsera","irk":""},{"addr":"55:A2:DC:A6:30:DB","name":"Apple Watch","irk":"00112233445566778899aabbccddeeff"}]}""");

        var result = await new BleBondClient(pipeName).ListAsync(TimeSpan.FromSeconds(5));

        await serverTask.WaitAsync(TestTimeout);
        Assert.Equal(2, result.Devices.Count);
        Assert.False(result.Devices[0].IsSupported);
        Assert.Equal("", result.Devices[0].Irk);
        Assert.True(result.Devices[1].IsSupported);
    }

    [Fact]
    public async Task List_devuelve_el_error_del_servicio()
    {
        var pipeName = NewPipeName();
        var serverTask = RunFakeServerAsync(
            pipeName,
            """{"t":"res","id":1,"ok":false,"error":"拒绝访问蓝牙配对库"}""");

        var result = await new BleBondClient(pipeName).ListAsync(TimeSpan.FromSeconds(5));

        await serverTask.WaitAsync(TestTimeout);
        Assert.False(result.Success);
        Assert.Equal("拒绝访问蓝牙配对库", result.Error);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public async Task List_reporta_respuesta_ilegible()
    {
        var pipeName = NewPipeName();
        var serverTask = RunFakeServerAsync(pipeName, "no es json");

        var result = await new BleBondClient(pipeName).ListAsync(TimeSpan.FromSeconds(5));

        await serverTask.WaitAsync(TestTimeout);
        Assert.False(result.Success);
        Assert.Contains("无法解析", result.Error);
    }

    [Fact]
    public async Task List_reporta_respuesta_con_tipo_desconocido()
    {
        var pipeName = NewPipeName();
        var serverTask = RunFakeServerAsync(pipeName, """{"t":"algo","id":1,"ok":true}""");

        var result = await new BleBondClient(pipeName).ListAsync(TimeSpan.FromSeconds(5));

        await serverTask.WaitAsync(TestTimeout);
        Assert.False(result.Success);
        Assert.Contains("无效响应", result.Error);
    }

    [Fact]
    public async Task List_sin_servicio_devuelve_error_de_tiempo_agotado()
    {
        var result = await new BleBondClient(NewPipeName())
            .ListAsync(TimeSpan.FromMilliseconds(200));

        Assert.False(result.Success);
        Assert.Contains("超时", result.Error);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void DisplayName_sin_nombre_muestra_solo_la_direccion()
    {
        Assert.Equal("55:A2:DC:A6:30:DB", new BleBondDevice("55:A2:DC:A6:30:DB", null, "00").DisplayName);
        Assert.Equal("55:A2:DC:A6:30:DB", new BleBondDevice("55:A2:DC:A6:30:DB", "  ", "00").DisplayName);
    }
}

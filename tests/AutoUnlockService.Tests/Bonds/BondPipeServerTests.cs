using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AutoUnlockService.Bonds;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AutoUnlockService.Tests.Bonds;

// Tuberia real (nombre unico por caso): peticion/response, error del lector y varios
// pedidos en una misma conexion. La DACL de produccion se verifica aparte
public class BondPipeServerTests : IDisposable
{
    private readonly TestGuard _guard = new();
    private CancellationToken Guard => _guard.Token;

    public void Dispose() => _guard.Dispose();

    private static PipeSecurity TestSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private static BondPipeServer NewServer(IBondedDeviceSource source, string pipeName) =>
        new(source, NullLogger<BondPipeServer>.Instance, pipeName, TestSecurity());

    private sealed class FakeBondSource(BondedDeviceReadResult result) : IBondedDeviceSource
    {
        public int Reads { get; private set; }

        public BondedDeviceReadResult ReadAll()
        {
            Reads++;
            return result;
        }
    }

    private sealed class BondTestClient : IAsyncDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        private BondTestClient(NamedPipeClientStream pipe)
        {
            _pipe = pipe;
            _reader = new StreamReader(
                pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096, leaveOpen: true);
            _writer = new StreamWriter(
                pipe, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true)
            {
                AutoFlush = true
            };
        }

        public static async Task<BondTestClient> ConnectAsync(string pipeName, CancellationToken ct)
        {
            var pipe = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(10_000, ct);
            return new BondTestClient(pipe);
        }

        public Task SendRawAsync(string line) => _writer.WriteLineAsync(line);

        public Task SendListAsync(int id) => _writer.WriteLineAsync(
            JsonSerializer.Serialize(new BondRequest("req", id, "list"), BondProtocol.JsonOptions));

        public async Task<BondResponse> ReadAsync(CancellationToken ct)
        {
            var line = await _reader.ReadLineAsync(ct);
            Assert.NotNull(line);
            return JsonSerializer.Deserialize<BondResponse>(line!, BondProtocol.JsonOptions)!;
        }

        public async ValueTask DisposeAsync()
        {
            _writer.Dispose();
            _reader.Dispose();
            await _pipe.DisposeAsync();
        }
    }

    [Fact]
    public void La_respuesta_usa_las_claves_camelCase_del_contrato_v1()
    {
        var line = BondProtocol.SerializeLine(BondProtocol.OkList(3, new[]
        {
            new BondedBleDevice("55:A2:DC:A6:30:DB", "Apple Watch", "00112233445566778899AABBCCDDEEFF"),
        }));

        Assert.EndsWith("\n", line);
        Assert.Contains("\"t\":\"res\"", line);
        Assert.Contains("\"id\":3", line);
        Assert.Contains("\"ok\":true", line);
        Assert.Contains(
            "\"devices\":[{\"addr\":\"55:A2:DC:A6:30:DB\",\"name\":\"Apple Watch\",\"irk\":\"00112233445566778899AABBCCDDEEFF\"}]",
            line);
        Assert.Contains("\"error\":null", line);
    }

    [Fact]
    public void La_DACL_de_produccion_solo_permite_a_SYSTEM_y_Administrators()
    {
        var rules = BondPipeServer.CreateDefaultSecurity()
            .GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>().ToList();

        Assert.Equal(2, rules.Count);
        Assert.All(rules, r =>
        {
            Assert.Equal(AccessControlType.Allow, r.AccessControlType);
            Assert.Equal(PipeAccessRights.FullControl, r.PipeAccessRights);
        });
        Assert.Contains(rules, r =>
            r.IdentityReference.Value == new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value);
        Assert.Contains(rules, r =>
            r.IdentityReference.Value == new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value);
    }

    [Fact]
    public async Task List_devuelve_los_dispositivos_emparejados()
    {
        var source = new FakeBondSource(new BondedDeviceReadResult(
            new[]
            {
                new BondedBleDevice("55:A2:DC:A6:30:DB", "Apple Watch", "00112233445566778899AABBCCDDEEFF"),
                new BondedBleDevice("59:93:E1:AA:B7:89", "iPhone", "FFEEDDCCBBAA99887766554433221100"),
            },
            null));

        var pipeName = TestSupport.UniquePipeName();
        var server = NewServer(source, pipeName);
        await server.StartAsync(Guard);
        try
        {
            await using var client = await BondTestClient.ConnectAsync(pipeName, Guard);
            await client.SendListAsync(1);

            var response = await client.ReadAsync(Guard);

            Assert.True(response.Ok);
            Assert.Equal(1, response.Id);
            Assert.Null(response.Error);
            Assert.NotNull(response.Devices);
            Assert.Equal(2, response.Devices!.Count);
            Assert.Equal("55:A2:DC:A6:30:DB", response.Devices[0].Addr);
            Assert.Equal("Apple Watch", response.Devices[0].Name);
            Assert.Equal("00112233445566778899AABBCCDDEEFF", response.Devices[0].Irk);
            Assert.Equal(1, source.Reads);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
            server.Dispose();
        }
    }

    [Fact]
    public async Task Un_error_del_lector_se_devuelve_como_respuesta_fallida()
    {
        var source = new FakeBondSource(new BondedDeviceReadResult(
            Array.Empty<BondedBleDevice>(), "拒绝访问蓝牙配对库"));

        var pipeName = TestSupport.UniquePipeName();
        var server = NewServer(source, pipeName);
        await server.StartAsync(Guard);
        try
        {
            await using var client = await BondTestClient.ConnectAsync(pipeName, Guard);
            await client.SendListAsync(2);

            var response = await client.ReadAsync(Guard);

            Assert.False(response.Ok);
            Assert.Equal(2, response.Id);
            Assert.Equal("拒绝访问蓝牙配对库", response.Error);
            Assert.Null(response.Devices);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
            server.Dispose();
        }
    }

    [Fact]
    public async Task Operacion_desconocida_devuelve_error_con_el_mismo_id()
    {
        var source = new FakeBondSource(new BondedDeviceReadResult(Array.Empty<BondedBleDevice>(), null));

        var pipeName = TestSupport.UniquePipeName();
        var server = NewServer(source, pipeName);
        await server.StartAsync(Guard);
        try
        {
            await using var client = await BondTestClient.ConnectAsync(pipeName, Guard);
            await client.SendRawAsync("""{"t":"req","id":7,"op":"nope"}""");

            var response = await client.ReadAsync(Guard);

            Assert.False(response.Ok);
            Assert.Equal(7, response.Id);
            Assert.Contains("nope", response.Error);
            Assert.Equal(0, source.Reads);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
            server.Dispose();
        }
    }

    [Fact]
    public async Task JSON_invalido_devuelve_error_con_id_cero()
    {
        var source = new FakeBondSource(new BondedDeviceReadResult(Array.Empty<BondedBleDevice>(), null));

        var pipeName = TestSupport.UniquePipeName();
        var server = NewServer(source, pipeName);
        await server.StartAsync(Guard);
        try
        {
            await using var client = await BondTestClient.ConnectAsync(pipeName, Guard);
            await client.SendRawAsync("esto no es json");

            var response = await client.ReadAsync(Guard);

            Assert.False(response.Ok);
            Assert.Equal(0, response.Id);
            Assert.False(string.IsNullOrWhiteSpace(response.Error));
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
            server.Dispose();
        }
    }

    [Fact]
    public async Task Varias_peticiones_en_la_misma_conexion()
    {
        var source = new FakeBondSource(new BondedDeviceReadResult(
            new[] { new BondedBleDevice("55:A2:DC:A6:30:DB", "Apple Watch", "00112233445566778899AABBCCDDEEFF") },
            null));

        var pipeName = TestSupport.UniquePipeName();
        var server = NewServer(source, pipeName);
        await server.StartAsync(Guard);
        try
        {
            await using var client = await BondTestClient.ConnectAsync(pipeName, Guard);

            await client.SendListAsync(10);
            var first = await client.ReadAsync(Guard);
            await client.SendListAsync(11);
            var second = await client.ReadAsync(Guard);

            Assert.True(first.Ok);
            Assert.True(second.Ok);
            Assert.Equal(10, first.Id);
            Assert.Equal(11, second.Id);
            Assert.Equal(2, source.Reads);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
            server.Dispose();
        }
    }
}

using System.Text.Json;
using BleUnlockService.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BleUnlockService.Tests;

public sealed class ReloadStatusFileTests : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);
    private readonly string _dir = Directory.CreateTempSubdirectory("reload-status-").FullName;
    private readonly FakeTimeProvider _time = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string StatusPath => Path.Combine(_dir, "reload-status.json");

    private static BleUnlockOptions Options(string irk = "ec0234a357c8ad05341010a60a397d9b", string user = "Admin") => new()
    {
        Device = new DeviceOptions { Irk = irk },
        Credentials = new CredentialOptions { UserName = user, Password = "p" },
    };

    private OptionsGate Gate() =>
        new(Options(), NullLogger<OptionsGate>.Instance, _time, Debounce, StatusPath);

    private JsonElement Apply(OptionsGate gate, BleUnlockOptions candidate)
    {
        gate.Submit(candidate);
        _time.Advance(Debounce + TimeSpan.FromMilliseconds(1));
        return JsonDocument.Parse(File.ReadAllText(StatusPath)).RootElement;
    }

    [Fact]
    public void Una_configuracion_aplicada_deja_resultado_y_cambios()
    {
        var status = Apply(Gate(), Options(user: "Otro"));

        Assert.Equal("applied", status.GetProperty("result").GetString());
        Assert.False(status.GetProperty("deviceChanged").GetBoolean());
        Assert.True(status.GetProperty("credentialsChanged").GetBoolean());
        Assert.Equal(32, status.GetProperty("id").GetString()!.Length);
    }

    [Fact]
    public void Una_configuracion_rechazada_deja_el_motivo()
    {
        var status = Apply(Gate(), Options(irk: "no-es-hexadecimal"));

        Assert.Equal("rejected", status.GetProperty("result").GetString());
        Assert.False(string.IsNullOrWhiteSpace(status.GetProperty("message").GetString()));
    }

    [Fact]
    public void Sin_cambios_queda_unchanged()
    {
        var status = Apply(Gate(), Options());

        Assert.Equal("unchanged", status.GetProperty("result").GetString());
    }

    [Fact]
    public void Cada_escritura_tiene_un_id_nuevo()
    {
        var gate = Gate();
        var first = Apply(gate, Options()).GetProperty("id").GetString();
        var second = Apply(gate, Options()).GetProperty("id").GetString();

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Sin_ruta_no_escribe_nada()
    {
        var gate = new OptionsGate(Options(), NullLogger<OptionsGate>.Instance, _time, Debounce);
        gate.Submit(Options(user: "Otro"));
        _time.Advance(Debounce + TimeSpan.FromMilliseconds(1));

        Assert.False(File.Exists(StatusPath));
    }
}

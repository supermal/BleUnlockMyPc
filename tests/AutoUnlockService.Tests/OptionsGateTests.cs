using AutoUnlockService.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AutoUnlockService.Tests;

public class OptionsGateTests
{
    private const string IrkA = "ec0234a357c8ad05341010a60a397d9b";
    private const string IrkB = "ec0234a357c8ad05341010a60a397d9a";
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    private static AutoUnlockOptions Options(string irk = IrkA, string user = "Admin", string pass = "p") => new()
    {
        Device = new DeviceOptions { Irk = irk },
        Credentials = new CredentialOptions { UserName = user, Password = pass }
    };

    private static (OptionsGate Gate, FakeTimeProvider Time, List<OptionsChange> Changes) Build()
    {
        var time = new FakeTimeProvider();
        var gate = new OptionsGate(Options(), NullLogger<OptionsGate>.Instance, time, Debounce);
        var changes = new List<OptionsChange>();
        gate.Changed += changes.Add;
        return (gate, time, changes);
    }

    [Fact]
    public void Varios_envios_seguidos_producen_un_solo_evento()
    {
        var (gate, time, changes) = Build();

        gate.Submit(Options(irk: IrkB));
        time.Advance(TimeSpan.FromMilliseconds(100));
        gate.Submit(Options(irk: IrkB));
        time.Advance(TimeSpan.FromMilliseconds(100));
        gate.Submit(Options(irk: IrkB));

        Assert.Empty(changes);                     // todavia dentro del antirrebote

        time.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Single(changes);
    }

    [Fact]
    public void Una_configuracion_invalida_no_sustituye_a_la_vigente()
    {
        var (gate, time, changes) = Build();
        var antes = gate.Current;

        gate.Submit(Options(irk: "no-es-hexadecimal"));
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Empty(changes);
        Assert.Same(antes, gate.Current);
    }

    [Fact]
    public void Un_fichero_corrupto_con_credenciales_vacias_se_rechaza()
    {
        var (gate, time, changes) = Build();
        var antes = gate.Current;

        // Lo que entrega IOptionsMonitor cuando el JSON deja de ser valido
        gate.Submit(new AutoUnlockOptions());
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Empty(changes);
        Assert.Same(antes, gate.Current);
        Assert.Equal("Admin", gate.Current.Credentials.UserName);
    }

    [Fact]
    public void Un_cambio_valido_sustituye_la_configuracion_vigente()
    {
        var (gate, time, changes) = Build();

        gate.Submit(Options(irk: IrkB));
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Single(changes);
        Assert.Equal(IrkB, gate.Current.Device.Irk);
    }

    [Fact]
    public void Marca_el_cambio_de_dispositivo()
    {
        var (gate, time, changes) = Build();

        gate.Submit(Options(irk: IrkB));
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.True(changes[0].DeviceChanged);
        Assert.False(changes[0].CredentialsChanged);
    }

    [Fact]
    public void Marca_el_cambio_de_credenciales()
    {
        var (gate, time, changes) = Build();

        gate.Submit(Options(pass: "otra"));
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.True(changes[0].CredentialsChanged);
        Assert.False(changes[0].DeviceChanged);
    }

    [Fact]
    public void Un_cambio_solo_de_umbrales_no_marca_ni_dispositivo_ni_credenciales()
    {
        var (gate, time, changes) = Build();
        var nuevo = Options();
        nuevo.Proximity.UnlockThresholdDbm = -40;

        gate.Submit(nuevo);
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Single(changes);
        Assert.False(changes[0].DeviceChanged);
        Assert.False(changes[0].CredentialsChanged);
        Assert.Equal(-40, gate.Current.Proximity.UnlockThresholdDbm);
    }

    [Fact]
    public void Un_envio_identico_al_vigente_no_dispara_evento()
    {
        var (gate, time, changes) = Build();

        gate.Submit(Options());
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Empty(changes);
    }

    [Fact]
    public void Tras_rechazar_una_invalida_sigue_aceptando_la_siguiente_valida()
    {
        var (gate, time, changes) = Build();

        gate.Submit(Options(irk: "malo"));
        time.Advance(TimeSpan.FromSeconds(1));
        gate.Submit(Options(irk: IrkB));
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Single(changes);
        Assert.Equal(IrkB, gate.Current.Device.Irk);
    }
}

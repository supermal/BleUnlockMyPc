using AutoUnlockService.Config;
using Microsoft.Extensions.Logging;
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
        => Build(NullLogger<OptionsGate>.Instance);

    private static (OptionsGate Gate, FakeTimeProvider Time, List<OptionsChange> Changes) Build(
        ILogger<OptionsGate> log)
    {
        var time = new FakeTimeProvider();
        var gate = new OptionsGate(Options(), log, time, Debounce);
        var changes = new List<OptionsChange>();
        gate.Changed += changes.Add;
        return (gate, time, changes);
    }

    [Fact]
    public void El_antirrebote_se_reinicia_y_gana_el_ultimo_candidato()
    {
        var (gate, time, changes) = Build();

        gate.Submit(Options(irk: IrkB));                 // A, vencera en t=250 si no se reinicia
        time.Advance(TimeSpan.FromMilliseconds(200));
        gate.Submit(Options(user: "Otro"));              // B, vencera en t=450

        // t=300: un temporizador sin reinicio ya habria aplicado A
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Empty(changes);

        // t=460: vencio el temporizador reiniciado
        time.Advance(TimeSpan.FromMilliseconds(160));
        Assert.Single(changes);
        Assert.Equal("Otro", gate.Current.Credentials.UserName);
        Assert.Equal(IrkA, gate.Current.Device.Irk);     // A nunca se aplico
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
    public void Un_cambio_solo_de_los_conteos_dispara_recarga_sin_marcar_dispositivo_ni_credenciales()
    {
        var (gate, time, changes) = Build();
        var nuevo = Options();
        nuevo.Proximity.LockConfirmSampleCount = 12;

        gate.Submit(nuevo);
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Single(changes);
        Assert.False(changes[0].DeviceChanged);
        Assert.False(changes[0].CredentialsChanged);
        Assert.Equal(12, gate.Current.Proximity.LockConfirmSampleCount);
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

    [Fact]
    public void Un_envio_tras_Dispose_no_hace_nada()
    {
        var (gate, time, changes) = Build();

        gate.Dispose();
        gate.Submit(Options(irk: IrkB));
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Empty(changes);
        Assert.Equal(IrkA, gate.Current.Device.Irk);
    }

    [Fact]
    public void Un_candidato_cifrado_se_descifra_antes_de_sustituir()
    {
        var (gate, time, changes) = Build();

        gate.Submit(Options(irk: SecretProtector.Protect(IrkB), pass: SecretProtector.Protect("otra")));
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Single(changes);
        Assert.Equal(IrkB, gate.Current.Device.Irk);             // 运行态只见明文
        Assert.Equal("otra", gate.Current.Credentials.Password); // 同上
        Assert.True(changes[0].DeviceChanged);
        Assert.True(changes[0].CredentialsChanged);
    }

    [Fact]
    public void Un_cifrado_ilegible_se_rechaza_y_conserva_el_vigente()
    {
        var log = new ListLogger<OptionsGate>();
        var (gate, time, changes) = Build(log);
        var antes = gate.Current;

        gate.Submit(Options(irk: SecretTestData.Tampered(SecretProtector.Protect(IrkB))));
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Empty(changes);
        Assert.Same(antes, gate.Current);
        Assert.Contains(log.Messages, m => m.Contains("配置校验失败，已保留原配置：Device.Irk 无法解密"));
    }

    [Fact]
    public void El_texto_plano_solo_deja_aviso_y_se_aplica()
    {
        var log = new ListLogger<OptionsGate>();
        var (gate, time, _) = Build(log);

        gate.Submit(Options(irk: IrkB));
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(IrkB, gate.Current.Device.Irk);
        Assert.Contains(log.Messages, m => m.Contains("Device.Irk 以明文存储"));
    }
}

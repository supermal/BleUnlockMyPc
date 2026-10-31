using System.Globalization;
using AutoUnlock.Shared;
using AutoUnlockService.Config;
using AutoUnlockService.Localization;
using AutoUnlockService.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AutoUnlockService.Tests;

public class HelperProtocolLanguageTests
{
    [Fact]
    public void HELLO_con_cultura_ida_y_vuelta()
    {
        Assert.Equal("HELLO 3 zh-CN\n", HelperProtocol.BuildHello(3, "zh-CN"));
        Assert.True(HelperProtocol.TryParseHello("HELLO 3 zh-CN", out var session, out var culture));
        Assert.Equal(3, session);
        Assert.Equal("zh-CN", culture);
    }

    [Fact]
    public void HELLO_sin_cultura_sigue_valiendo()
    {
        Assert.Equal("HELLO 3\n", HelperProtocol.BuildHello(3, null));
        Assert.True(HelperProtocol.TryParseHello("HELLO 3", out var session, out var culture));
        Assert.Equal(3, session);
        Assert.Null(culture);
    }

    [Fact]
    public void HELLO_con_cultura_extrana_conserva_la_sesion()
    {
        Assert.True(HelperProtocol.TryParseHello("HELLO 5 ¿¿ basura", out var session, out var culture));
        Assert.Equal(5, session);
        Assert.Equal("¿¿ basura", culture);
    }

    [Theory]
    [InlineData("HELLO")]
    [InlineData("HELLO x")]
    [InlineData("HOLA 1")]
    public void HELLO_mal_formado_se_rechaza(string line)
        => Assert.False(HelperProtocol.TryParseHello(line, out _, out _));

    [Theory]
    [InlineData(AppLanguage.Chinese, "LANG zh\n")]
    [InlineData(AppLanguage.Spanish, "LANG es\n")]
    public void LANG_ida_y_vuelta(AppLanguage language, string line)
    {
        Assert.Equal(line, HelperProtocol.BuildLanguage(language));
        Assert.True(HelperProtocol.TryParseCommand(line, out var command));
        Assert.Equal(new HelperCommand.Language(language), command);
    }

    [Theory]
    [InlineData("LANG fr")]
    [InlineData("LANG")]
    public void LANG_desconocido_se_rechaza(string line)
        => Assert.False(HelperProtocol.TryParseCommand(line, out _));
}

[Collection(TestCulture.LanguageCollection)]
public sealed class HelperLanguageRelayTests : IDisposable
{
    private readonly TestGuard _guard = new();
    private readonly FakeTimeProvider _time = new();

    public void Dispose()
    {
        _guard.Dispose();
        TestCulture.Reset();
    }

    private OptionsGate Gate(string language) => new(
        new AutoUnlockOptions
        {
            Device = new DeviceOptions { Irk = "ec0234a357c8ad05341010a60a397d9b" },
            Credentials = new CredentialOptions { UserName = "Admin", Password = "p" },
            Ui = new UiOptions { Language = language },
        },
        NullLogger<OptionsGate>.Instance, _time, TimeSpan.FromMilliseconds(250));

    [Fact]
    public async Task Al_conectar_el_asistente_recibe_el_idioma_de_su_usuario()
    {
        var name = TestSupport.UniquePipeName();
        await using var server = new HelperPipeServer(NullLogger<HelperPipeServer>.Instance, null, name);
        var language = new ServiceLanguage(Gate("system"), new CultureInfo("en-US"));
        using var relay = new HelperLanguageRelay(server, language, NullLogger<HelperLanguageRelay>.Instance);
        await relay.StartAsync(CancellationToken.None);
        server.Start(CancellationToken.None);

        await using var client = await FakeHelperClient.ConnectAsync(name, sessionId: 1, culture: "es-ES");

        Assert.Equal(new HelperCommand.Language(AppLanguage.Spanish), await client.ReadCommandAsync(_guard.Token));
        Assert.Equal(AppLanguage.Spanish, language.Current);
    }

    [Fact]
    public async Task Un_cambio_de_idioma_se_envia_al_asistente_conectado()
    {
        var name = TestSupport.UniquePipeName();
        await using var server = new HelperPipeServer(NullLogger<HelperPipeServer>.Instance, null, name);
        var gate = Gate("zh");
        var language = new ServiceLanguage(gate, new CultureInfo("en-US"));
        using var relay = new HelperLanguageRelay(server, language, NullLogger<HelperLanguageRelay>.Instance);
        await relay.StartAsync(CancellationToken.None);
        server.Start(CancellationToken.None);

        await using var client = await FakeHelperClient.ConnectAsync(name);
        Assert.Equal(new HelperCommand.Language(AppLanguage.Chinese), await client.ReadCommandAsync(_guard.Token));

        gate.Submit(new AutoUnlockOptions
        {
            Device = gate.Current.Device,
            Credentials = gate.Current.Credentials,
            Ui = new UiOptions { Language = "es" },
        });
        _time.Advance(TimeSpan.FromMilliseconds(300));

        Assert.Equal(new HelperCommand.Language(AppLanguage.Spanish), await client.ReadCommandAsync(_guard.Token));
    }
}

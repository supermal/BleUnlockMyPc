using System.Globalization;
using BleUnlock.Shared;
using BleUnlockService.Config;
using BleUnlockService.Localization;
using BleUnlockService.Resources;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BleUnlockService.Tests;

[Collection(TestCulture.LanguageCollection)]
public sealed class ServiceLanguageTests : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);
    private readonly FakeTimeProvider _time = new();

    public void Dispose() => TestCulture.Reset();

    private static BleUnlockOptions Options(string language) => new()
    {
        Device = new DeviceOptions { Irk = "ec0234a357c8ad05341010a60a397d9b" },
        Credentials = new CredentialOptions { UserName = "Admin", Password = "p" },
        Ui = new UiOptions { Language = language },
    };

    private OptionsGate Gate(string language) =>
        new(Options(language), NullLogger<OptionsGate>.Instance, _time, Debounce);

    [Fact]
    public void Antes_de_que_hable_el_asistente_usa_el_idioma_del_equipo()
    {
        var language = new ServiceLanguage(Gate("system"), new CultureInfo("es-ES"));

        Assert.Equal(AppLanguage.Spanish, language.Current);
        Assert.Equal("es", ServiceStrings.Culture?.Name);
    }

    [Fact]
    public void En_modo_system_manda_el_idioma_del_usuario()
    {
        var language = new ServiceLanguage(Gate("system"), new CultureInfo("es-ES"));
        var changes = new List<AppLanguage>();
        language.Changed += changes.Add;

        language.SetUserCulture("zh-CN");

        Assert.Equal(AppLanguage.Chinese, language.Current);
        Assert.Equal([AppLanguage.Chinese], changes);
    }

    [Fact]
    public void Un_idioma_explicito_ignora_al_usuario()
    {
        var language = new ServiceLanguage(Gate("en"), new CultureInfo("es-ES"));
        var changes = new List<AppLanguage>();
        language.Changed += changes.Add;

        language.SetUserCulture("zh-CN");

        Assert.Equal(AppLanguage.English, language.Current);
        Assert.Empty(changes);
    }

    [Fact]
    public void Cambiar_la_configuracion_cambia_el_idioma()
    {
        var gate = Gate("zh");
        var language = new ServiceLanguage(gate, new CultureInfo("en-US"));

        gate.Submit(Options("es"));
        _time.Advance(Debounce + TimeSpan.FromMilliseconds(1));

        Assert.Equal(AppLanguage.Spanish, language.Current);
        Assert.Equal("es", ServiceStrings.Culture?.Name);
    }

    [Theory]
    [InlineData("x!")]
    [InlineData("")]
    [InlineData(null)]
    public void Una_cultura_de_usuario_invalida_no_falla(string? culture)
    {
        var language = new ServiceLanguage(Gate("system"), new CultureInfo("es-ES"));

        language.SetUserCulture(culture);

        Assert.Equal(AppLanguage.Spanish, language.Current);
    }

    [Fact]
    public void Cambiar_solo_el_idioma_es_un_cambio_de_configuracion()
    {
        var gate = Gate("zh");
        var changes = new List<OptionsChange>();
        gate.Changed += changes.Add;

        gate.Submit(Options("en"));
        _time.Advance(Debounce + TimeSpan.FromMilliseconds(1));

        Assert.Single(changes);
        Assert.Equal("en", gate.Current.Ui.Language);
    }

    [Fact]
    public void Un_codigo_distinto_solo_en_mayusculas_no_es_un_cambio()
    {
        var gate = Gate("en");
        var changes = new List<OptionsChange>();
        gate.Changed += changes.Add;

        gate.Submit(Options(" EN "));
        _time.Advance(Debounce + TimeSpan.FromMilliseconds(1));

        Assert.Empty(changes);
    }
}

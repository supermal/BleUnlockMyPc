using System.Globalization;
using AutoUnlock.Shared;
using Xunit;

namespace AutoUnlockService.Tests;

public class AppLanguageTests
{
    private static CultureInfo C(string name) => new(name);

    [Fact]
    public void Resolve_usa_el_codigo_explicito()
        => Assert.Equal(AppLanguage.Spanish, AppLanguages.Resolve("es", C("zh-CN")));

    [Fact]
    public void Resolve_ignora_espacios_y_mayusculas()
        => Assert.Equal(AppLanguage.English, AppLanguages.Resolve(" EN ", C("es-ES")));

    [Theory]
    [InlineData("zh-CN", AppLanguage.Chinese)]
    [InlineData("zh-TW", AppLanguage.Chinese)]
    [InlineData("zh-HK", AppLanguage.Chinese)]
    [InlineData("en-GB", AppLanguage.English)]
    [InlineData("es-MX", AppLanguage.Spanish)]
    public void Resolve_system_sigue_la_cultura(string culture, AppLanguage expected)
        => Assert.Equal(expected, AppLanguages.Resolve("system", C(culture)));

    [Fact]
    public void Resolve_cultura_no_soportada_usa_ingles()
    {
        Assert.Equal(AppLanguage.English, AppLanguages.Resolve("system", C("fr-FR")));
        Assert.Equal(AppLanguage.English, AppLanguages.Resolve(null, C("de-DE")));
    }

    [Fact]
    public void Resolve_codigo_invalido_sigue_al_sistema()
        => Assert.Equal(AppLanguage.Spanish, AppLanguages.Resolve("fr", C("es-ES")));

    [Theory]
    [InlineData(AppLanguage.Chinese, "zh")]
    [InlineData(AppLanguage.English, "en")]
    [InlineData(AppLanguage.Spanish, "es")]
    public void ToCode_y_TryParseCode_son_simetricos(AppLanguage language, string code)
    {
        Assert.Equal(code, AppLanguages.ToCode(language));
        Assert.True(AppLanguages.TryParseCode(code, out var parsed));
        Assert.Equal(language, parsed);
    }

    [Fact]
    public void ToCulture_devuelve_la_cultura_neutra()
    {
        Assert.Equal("zh", AppLanguages.ToCulture(AppLanguage.Chinese).Name);
        Assert.Equal("en", AppLanguages.ToCulture(AppLanguage.English).Name);
        Assert.Equal("es", AppLanguages.ToCulture(AppLanguage.Spanish).Name);
    }
}

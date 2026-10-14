using AutoUnlockService.Credentials;
using Xunit;

namespace AutoUnlockService.Tests;

public class UserNameParserTests
{
    [Fact]
    public void Una_cuenta_local_usa_el_punto_como_dominio()
    {
        var name = UserNameParser.Split("Admin");

        Assert.Equal(".", name.Domain);
        Assert.Equal("Admin", name.User);
    }

    [Fact]
    public void Una_cuenta_de_dominio_se_parte_por_la_barra()
    {
        var name = UserNameParser.Split("KIROL\\hq.qiu");

        Assert.Equal("KIROL", name.Domain);
        Assert.Equal("hq.qiu", name.User);
    }

    [Fact]
    public void Una_cuenta_microsoft_conserva_el_correo_entero_como_usuario()
    {
        var name = UserNameParser.Split("MicrosoftAccount\\alguien@outlook.com");

        Assert.Equal("MicrosoftAccount", name.Domain);
        Assert.Equal("alguien@outlook.com", name.User);
    }

    [Fact]
    public void Un_upn_va_sin_dominio_porque_LogonUser_lo_resuelve_solo()
    {
        var name = UserNameParser.Split("hq.qiu@kirol.es");

        Assert.Null(name.Domain);
        Assert.Equal("hq.qiu@kirol.es", name.User);
    }

    [Fact]
    public void Solo_se_parte_por_la_primera_barra()
    {
        var name = UserNameParser.Split("DOM\\sub\\user");

        Assert.Equal("DOM", name.Domain);
        Assert.Equal("sub\\user", name.User);
    }

    [Fact]
    public void Se_recortan_los_espacios_sobrantes()
    {
        var name = UserNameParser.Split("  Admin  ");

        Assert.Equal(".", name.Domain);
        Assert.Equal("Admin", name.User);
    }

    [Fact]
    public void Una_barra_inicial_no_deja_un_dominio_vacio()
    {
        var name = UserNameParser.Split("\\Admin");

        Assert.Equal(".", name.Domain);
        Assert.Equal("Admin", name.User);
    }
}

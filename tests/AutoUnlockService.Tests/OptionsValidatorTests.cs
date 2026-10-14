using AutoUnlockService.Config;
using Xunit;

namespace AutoUnlockService.Tests;

public class OptionsValidatorTests
{
    private static AutoUnlockOptions Valid() => new()
    {
        Device = new DeviceOptions { Irk = "ec0234a357c8ad05341010a60a397d9b" },
        Credentials = new CredentialOptions { UserName = "Admin", Password = "secret" }
    };

    [Theory]
    [InlineData("ec0234a357c8ad05341010a60a397d9b")]
    [InlineData("EC0234A357C8AD05341010A60A397D9B")]
    [InlineData("ec:02:34:a3:57:c8:ad:05:34:10:10:a6:0a:39:7d:9b")]
    [InlineData("0xec0234a357c8ad05341010a60a397d9b")]
    [InlineData(" ec0234a357c8ad05341010a60a397d9b ")]
    public void NormalizeIrk_acepta_formatos_validos(string raw)
        => Assert.Equal("ec0234a357c8ad05341010a60a397d9b", OptionsValidator.NormalizeIrk(raw));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("ec0234a357c8ad05341010a60a397d9")]    // 31 caracteres
    [InlineData("ec0234a357c8ad05341010a60a397d9bb")]  // 33 caracteres
    [InlineData("zz0234a357c8ad05341010a60a397d9b")]   // no hexadecimal
    public void NormalizeIrk_rechaza_formatos_invalidos(string? raw)
        => Assert.Null(OptionsValidator.NormalizeIrk(raw));

    [Fact]
    public void Validate_acepta_configuracion_correcta()
        => Assert.True(OptionsValidator.Validate(Valid()).IsValid);

    [Fact]
    public void Validate_rechaza_usuario_con_salto_de_linea()
    {
        var o = Valid();
        o.Credentials.UserName = "Ad\nmin";
        var r = OptionsValidator.Validate(o);
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("UserName") && e.Contains("控制字符"));
    }

    [Theory]
    [InlineData("cla\nve")]
    [InlineData("cla\rve")]
    [InlineData("cla\tve")]
    public void Validate_rechaza_clave_con_caracteres_de_control(string password)
    {
        var o = Valid();
        o.Credentials.Password = password;
        var r = OptionsValidator.Validate(o);
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("Password") && e.Contains("控制字符"));
        // El error nombra el campo, nunca el valor
        Assert.DoesNotContain(r.Errors, e => e.Contains(password));
    }

    [Fact]
    public void Validate_acepta_clave_con_puntuacion_y_no_ascii()
    {
        var o = Valid();
        o.Credentials.Password = "P@ss w0rd!#$%^&*()_+-=[]{};:'\",.<>/?|~`contraseña密码😀";
        Assert.True(OptionsValidator.Validate(o).IsValid);
    }

    [Fact]
    public void Validate_rechaza_irk_invalido()
    {
        var o = Valid();
        o.Device.Irk = "abc";
        var r = OptionsValidator.Validate(o);
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("Irk"));
    }

    [Fact]
    public void Validate_rechaza_umbral_de_desbloqueo_no_mayor_que_el_de_bloqueo()
    {
        var o = Valid();
        o.Proximity.UnlockThresholdDbm = -90;   // menor que LockThresholdDbm (-80)
        var r = OptionsValidator.Validate(o);
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("UnlockThresholdDbm"));
    }

    [Fact]
    public void Validate_rechaza_umbrales_iguales()
    {
        var o = Valid();
        o.Proximity.UnlockThresholdDbm = -80;   // igual a LockThresholdDbm
        Assert.False(OptionsValidator.Validate(o).IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_rechaza_ventanas_no_positivas(double seconds)
    {
        var o = Valid();
        o.Proximity.LockWindowSeconds = seconds;
        Assert.False(OptionsValidator.Validate(o).IsValid);
    }

    [Fact]
    public void Validate_rechaza_min_samples_menor_que_uno()
    {
        var o = Valid();
        o.Proximity.MinSamples = 0;
        Assert.False(OptionsValidator.Validate(o).IsValid);
    }

    [Fact]
    public void Validate_rechaza_usuario_vacio()
    {
        var o = Valid();
        o.Credentials.UserName = "   ";
        var r = OptionsValidator.Validate(o);
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("UserName"));
    }

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF", true)]
    [InlineData("aabbccddeeff", true)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("AA:BB:CC:DD:EE", false)]
    [InlineData("GG:BB:CC:DD:EE:FF", false)]
    public void Validate_comprueba_identity_address(string? address, bool expected)
    {
        var o = Valid();
        o.Device.IdentityAddress = address;
        Assert.Equal(expected, OptionsValidator.Validate(o).IsValid);
    }

    [Fact]
    public void Validate_acumula_varios_errores()
    {
        var o = Valid();
        o.Device.Irk = "abc";
        o.Credentials.UserName = "";
        Assert.True(OptionsValidator.Validate(o).Errors.Count >= 2);
    }
}

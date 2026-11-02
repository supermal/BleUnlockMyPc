using BleUnlockService.Config;
using BleUnlockService.Session;
using Xunit;

namespace BleUnlockService.Tests;

public class OptionsValidatorTests
{
    private static BleUnlockOptions Valid() => new()
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
    [InlineData(21)]
    public void Validate_rechaza_UnlockSampleCount_fuera_de_rango(int count)
    {
        var o = Valid();
        o.Proximity.UnlockSampleCount = count;
        var r = OptionsValidator.Validate(o);
        Assert.False(r.IsValid);
        // 消息与镜像端逐字一致（MirrorDriftGuard 也会比对）
        Assert.Contains("Proximity.UnlockSampleCount 必须在 1 到 20 之间", r.Errors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void Validate_rechaza_LockPromptSampleCount_fuera_de_rango(int count)
    {
        var o = Valid();
        o.Proximity.LockPromptSampleCount = count;
        var r = OptionsValidator.Validate(o);
        Assert.False(r.IsValid);
        Assert.Contains("Proximity.LockPromptSampleCount 必须在 1 到 50 之间", r.Errors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void Validate_rechaza_LockConfirmSampleCount_fuera_de_rango(int count)
    {
        var o = Valid();
        o.Proximity.LockConfirmSampleCount = count;
        var r = OptionsValidator.Validate(o);
        Assert.False(r.IsValid);
        Assert.Contains("Proximity.LockConfirmSampleCount 必须在 1 到 50 之间", r.Errors);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2001)]
    public void Validate_rechaza_SampleIntervalMs_fuera_de_rango(int interval)
    {
        var o = Valid();
        o.Proximity.SampleIntervalMs = interval;
        var r = OptionsValidator.Validate(o);
        Assert.False(r.IsValid);
        Assert.Contains("Proximity.SampleIntervalMs 必须在 0 到 2000 之间（0 表示不限制）", r.Errors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2000)]
    public void Validate_acepta_los_limites_de_SampleIntervalMs(int interval)
    {
        var o = Valid();
        o.Proximity.SampleIntervalMs = interval;
        Assert.True(OptionsValidator.Validate(o).IsValid);
    }

    [Fact]
    public void Los_limites_exactos_de_los_conteos_se_aceptan()
    {
        var o = Valid();
        o.Proximity.UnlockSampleCount = 1;
        o.Proximity.LockPromptSampleCount = 1;
        o.Proximity.LockConfirmSampleCount = 1;
        Assert.True(OptionsValidator.Validate(o).IsValid);

        o.Proximity.UnlockSampleCount = 20;
        o.Proximity.LockPromptSampleCount = 50;
        o.Proximity.LockConfirmSampleCount = 50;
        Assert.True(OptionsValidator.Validate(o).IsValid);
    }

    [Fact]
    public void Validate_acepta_usuario_vacio_porque_solo_falta_por_configurar()
    {
        var o = Valid();
        o.Credentials.UserName = "   ";
        o.Credentials.Password = "";
        Assert.True(OptionsValidator.Validate(o).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_acepta_irk_vacio_porque_solo_falta_por_configurar(string irk)
    {
        var o = Valid();
        o.Device.Irk = irk;
        Assert.True(OptionsValidator.Validate(o).IsValid);
    }

    [Fact]
    public void Validate_acepta_la_configuracion_de_un_equipo_recien_instalado()
        => Assert.True(OptionsValidator.Validate(new BleUnlockOptions()).IsValid);

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
        o.Proximity.UnlockThresholdDbm = o.Proximity.LockThresholdDbm;
        Assert.True(OptionsValidator.Validate(o).Errors.Count >= 2);
    }

    [Fact]
    public void SnoozeMinutes_por_encima_del_maximo_se_rechaza_nombrando_campo_y_limite()
    {
        var o = Valid();
        o.Notification.SnoozeMinutes = HelperProtocol.MaxSnoozeMinutes + 1;

        var r = OptionsValidator.Validate(o);

        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("Notification.SnoozeMinutes") && e.Contains("240"));
    }

    [Fact]
    public void El_maximo_exacto_de_SnoozeMinutes_se_acepta()
    {
        var o = Valid();
        o.Notification.SnoozeMinutes = HelperProtocol.MaxSnoozeMinutes;

        Assert.True(OptionsValidator.Validate(o).IsValid);
    }

    [Fact]
    public void NaN_en_SnoozeMinutes_se_rechaza()
    {
        var o = Valid();
        o.Notification.SnoozeMinutes = double.NaN;

        Assert.True(OptionsValidator.Validate(o).Errors.Count >= 1);
    }
}

using System.Globalization;
using BleUnlockService.Config;
using BleUnlockService.Core;
using BleUnlockService.Credentials;
using BleUnlockService.Resources;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BleUnlockService.Tests;

// Los textos del servicio (registro, validación, motivos de transición) siguen al idioma de ServiceStrings
[Collection(TestCulture.LanguageCollection)]
public sealed class ServiceTextLanguageTests : IDisposable
{
    public void Dispose() => TestCulture.Reset();

    private static void Use(string culture) => ServiceStrings.Culture = new CultureInfo(culture);

    private static BleUnlockOptions Options(string irk) => new()
    {
        Device = new DeviceOptions { Irk = irk },
        Credentials = new CredentialOptions { UserName = "Admin", Password = "p" },
    };

    [Fact]
    public void Los_errores_de_validacion_siguen_el_idioma()
    {
        Use("es");
        var result = OptionsValidator.Validate(Options("no-es-hexadecimal"));

        Assert.Contains(result.Errors, e => e.Contains("hexadecimales"));
    }

    [Fact]
    public void El_registro_de_rechazo_sigue_el_idioma_y_conserva_la_propiedad()
    {
        Use("en");
        var time = new FakeTimeProvider();
        var log = new ListLogger<OptionsGate>();
        var gate = new OptionsGate(Options("ec0234a357c8ad05341010a60a397d9b"), log, time, TimeSpan.FromMilliseconds(250));

        gate.Submit(Options("bad"));
        time.Advance(TimeSpan.FromMilliseconds(300));

        Assert.Contains(log.Messages, m => m.StartsWith("Configuration validation failed", StringComparison.Ordinal)
                                            && m.Contains("Device.Irk"));
    }

    [Fact]
    public void La_descripcion_de_NTSTATUS_sigue_el_idioma()
    {
        Use("es");

        Assert.Equal("La cuenta está bloqueada", NtStatusPolicy.Describe(NtStatusPolicy.StatusAccountLockedOut));
        Assert.Equal("Error al iniciar sesión (NTSTATUS 0x12345678)", NtStatusPolicy.Describe(0x12345678u));
    }

    [Fact]
    public void Chino_sigue_siendo_identico_al_texto_original()
    {
        Use("zh");

        Assert.Equal("账户已被锁定", NtStatusPolicy.Describe(NtStatusPolicy.StatusAccountLockedOut));
    }
}

using BleUnlockService.Ble;
using BleUnlockService.Config;
using BleUnlockService.Core;
using Xunit;

namespace BleUnlockService.Tests;

public class ConfigCompletenessTests
{
    private const string Irk = "ec0234a357c8ad05341010a60a397d9b";

    private static BleUnlockOptions Options(string irk = Irk, string? address = null, string user = "Admin", string pass = "p") => new()
    {
        Device = new DeviceOptions { Irk = irk, IdentityAddress = address },
        Credentials = new CredentialOptions { UserName = user, Password = pass }
    };

    [Fact]
    public void Un_equipo_recien_instalado_no_tiene_dispositivo()
    {
        Assert.Equal(SetupState.NoDevice, ConfigCompleteness.Evaluate(new BleUnlockOptions()));
        Assert.Equal("noDevice", ConfigCompleteness.ToWire(SetupState.NoDevice));
    }

    [Fact]
    public void El_irk_de_relleno_no_cuenta_como_dispositivo()
        => Assert.Equal(SetupState.NoDevice, ConfigCompleteness.Evaluate(Options(irk: ConfigCompleteness.PlaceholderIrk)));

    [Fact]
    public void Una_direccion_de_identidad_basta_como_dispositivo()
        => Assert.True(ConfigCompleteness.HasDevice(Options(irk: "", address: "AA:BB:CC:DD:EE:FF").Device));

    [Theory]
    [InlineData("", "p")]
    [InlineData("  ", "p")]
    [InlineData("Admin", "")]
    public void Sin_usuario_o_sin_clave_faltan_las_credenciales(string user, string pass)
    {
        Assert.Equal(SetupState.NoCredentials, ConfigCompleteness.Evaluate(Options(user: user, pass: pass)));
        Assert.Equal("noCredentials", ConfigCompleteness.ToWire(SetupState.NoCredentials));
    }

    [Fact]
    public void Con_todo_esta_completa()
    {
        Assert.Equal(SetupState.Complete, ConfigCompleteness.Evaluate(Options()));
        Assert.Null(ConfigCompleteness.ToWire(SetupState.Complete));
    }

    [Fact]
    public void El_buscador_no_reconoce_a_nadie_con_el_irk_de_relleno()
    {
        // Una RPA (bits 47-46 = 01) que sí se resuelve con el IRK todo ceros
        const uint prand = 0x4A1B2C;
        var address = ((ulong)prand << 24) | RpaResolver.ComputeHash(new byte[16], prand);
        var sample = new AdvertisementSample(address, true, -50, DateTimeOffset.UtcNow);

        var resolver = new RpaResolver();
        resolver.SetKey(new byte[16], IrkByteOrder.Normal);
        Assert.True(resolver.Matches(address, isRandom: true));

        var matcher = new DeviceMatcher();
        matcher.Configure(new DeviceOptions { Irk = ConfigCompleteness.PlaceholderIrk });
        Assert.False(matcher.IsTarget(sample));
    }
}

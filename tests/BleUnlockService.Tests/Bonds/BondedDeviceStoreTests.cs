using System.Text;
using BleUnlockService.Bonds;
using Xunit;

namespace BleUnlockService.Tests.Bonds;

// Pruebas puras del lector del almacen de emparejamiento de Windows:
// normalizacion de direcciones, decodificacion de nombres y filtro de IRK utilizables
public class BondedDeviceStoreTests
{
    [Theory]
    [InlineData("55A2DCA630DB", "55:A2:DC:A6:30:DB")]
    [InlineData("55:a2:dc:a6:30:db", "55:A2:DC:A6:30:DB")]
    [InlineData("55-A2-DC-A6-30-DB", "55:A2:DC:A6:30:DB")]
    [InlineData("55:A2:DC:A6:30:DB", "55:A2:DC:A6:30:DB")]
    public void Normaliza_direcciones_a_seis_bytes_con_dos_puntos(string input, string expected)
    {
        Assert.Equal(expected, WindowsBondedDeviceSource.NormalizeAddress(input));
    }

    [Theory]
    [InlineData("no-es-una-mac")]
    [InlineData("55A2DC")]
    [InlineData("ZZA2DCA630DB")]
    public void Deja_intactas_las_direcciones_no_reconocibles(string input)
    {
        Assert.Equal(input, WindowsBondedDeviceSource.NormalizeAddress(input));
    }

    [Fact]
    public void Decodifica_nombres_reg_binary_utf8_con_nul_final()
    {
        var bytes = Encoding.UTF8.GetBytes("Apple Watch\0");
        Assert.Equal("Apple Watch", WindowsBondedDeviceSource.DecodeRegistryName(bytes));
    }

    [Fact]
    public void Decodifica_nombres_de_texto_y_arreglos()
    {
        Assert.Equal("iPhone", WindowsBondedDeviceSource.DecodeRegistryName("iPhone"));
        Assert.Equal("Mi Band", WindowsBondedDeviceSource.DecodeRegistryName(new[] { "Mi", "Band" }));
        Assert.Null(WindowsBondedDeviceSource.DecodeRegistryName(null));
        Assert.Null(WindowsBondedDeviceSource.DecodeRegistryName(Array.Empty<byte>()));
        Assert.Null(WindowsBondedDeviceSource.DecodeRegistryName("   "));
    }

    [Fact]
    public void Acepta_IRK_de_16_bytes_que_no_sea_todo_cero_ni_todo_FF()
    {
        Assert.True(WindowsBondedDeviceSource.IsUsableIrk(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray()));
        Assert.False(WindowsBondedDeviceSource.IsUsableIrk(null));
        Assert.False(WindowsBondedDeviceSource.IsUsableIrk(new byte[15]));
        Assert.False(WindowsBondedDeviceSource.IsUsableIrk(new byte[16]));
        Assert.False(WindowsBondedDeviceSource.IsUsableIrk(Enumerable.Repeat((byte)0xFF, 16).ToArray()));
    }

    [Fact]
    public void DisplayName_cae_a_la_direccion_cuando_no_hay_nombre()
    {
        Assert.Equal("Apple Watch", new BondedBleDevice("55:A2:DC:A6:30:DB", "Apple Watch", "00").DisplayName);
        Assert.Equal("55:A2:DC:A6:30:DB", new BondedBleDevice("55:A2:DC:A6:30:DB", null, "00").DisplayName);
        Assert.Equal("55:A2:DC:A6:30:DB", new BondedBleDevice("55:A2:DC:A6:30:DB", "  ", "00").DisplayName);
    }
}

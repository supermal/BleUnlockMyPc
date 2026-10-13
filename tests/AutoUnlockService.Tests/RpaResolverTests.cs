using AutoUnlockService.Config;
using AutoUnlockService.Core;
using Xunit;

namespace AutoUnlockService.Tests;

public class RpaResolverTests
{
    // Vector de prueba oficial: Bluetooth Core Spec Vol 3 Part H, Apendice D.7
    internal static readonly byte[] SpecIrk = Convert.FromHexString("ec0234a357c8ad05341010a60a397d9b");
    internal const ulong SpecRpa = 0x708194_0DFBAAUL;

    internal static ulong MakeRpa(byte[] irk, uint prand) =>
        ((ulong)prand << 24) | RpaResolver.ComputeHash(irk, prand);

    [Fact]
    public void ComputeHash_coincide_con_el_vector_oficial()
        => Assert.Equal(0x0DFBAAu, RpaResolver.ComputeHash(SpecIrk, 0x708194));

    [Theory]
    [InlineData(IrkByteOrder.Normal)]
    [InlineData(IrkByteOrder.Auto)]
    public void Matches_con_irk_en_orden_normal(IrkByteOrder order)
    {
        var r = new RpaResolver();
        r.SetKey(SpecIrk, order);

        Assert.True(r.Matches(SpecRpa, isRandom: true));
        Assert.Equal(IrkByteOrder.Normal, r.MatchedOrder);
    }

    [Theory]
    [InlineData(IrkByteOrder.Reversed)]
    [InlineData(IrkByteOrder.Auto)]
    public void Matches_con_irk_introducida_al_reves(IrkByteOrder order)
    {
        var r = new RpaResolver();
        r.SetKey(Enumerable.Reverse(SpecIrk).ToArray(), order);

        Assert.True(r.Matches(SpecRpa, isRandom: true));
        Assert.Equal(IrkByteOrder.Reversed, r.MatchedOrder);
    }

    [Fact]
    public void Matches_modo_normal_no_acepta_entrada_invertida()
    {
        var r = new RpaResolver();
        r.SetKey(Enumerable.Reverse(SpecIrk).ToArray(), IrkByteOrder.Normal);

        Assert.False(r.Matches(SpecRpa, isRandom: true));
    }

    [Fact]
    public void Matches_irk_con_un_bit_cambiado_no_coincide()
    {
        var wrong = SpecIrk.ToArray();
        wrong[0] ^= 0x01;
        var r = new RpaResolver();
        r.SetKey(wrong, IrkByteOrder.Auto);

        Assert.False(r.Matches(SpecRpa, isRandom: true));
    }

    [Theory]
    [InlineData(SpecRpa, false)]            // direccion publica
    [InlineData(0xC08194_0DFBAAUL, true)]   // aleatoria estatica
    [InlineData(0x008194_0DFBAAUL, true)]   // no resoluble
    public void Matches_direccion_no_resoluble_devuelve_false(ulong address, bool isRandom)
    {
        var r = new RpaResolver();
        r.SetKey(SpecIrk, IrkByteOrder.Auto);

        Assert.False(r.Matches(address, isRandom));
    }

    [Fact]
    public void Matches_segunda_rpa_de_la_misma_irk_tambien_coincide()
    {
        var r = new RpaResolver();
        r.SetKey(SpecIrk, IrkByteOrder.Auto);

        Assert.True(r.Matches(MakeRpa(SpecIrk, 0x4ABCDE), isRandom: true));
    }

    [Fact]
    public void SetKey_limpia_los_fallos_cacheados()
    {
        var r = new RpaResolver();
        var wrong = SpecIrk.ToArray();
        wrong[0] ^= 0x01;
        r.SetKey(wrong, IrkByteOrder.Auto);
        Assert.False(r.Matches(SpecRpa, isRandom: true));

        r.SetKey(SpecIrk, IrkByteOrder.Auto);
        Assert.True(r.Matches(SpecRpa, isRandom: true));
    }

    [Fact]
    public void SetKey_rechaza_irk_de_longitud_incorrecta()
    {
        var r = new RpaResolver();
        Assert.Throws<ArgumentException>(() => r.SetKey(new byte[15], IrkByteOrder.Auto));
    }

    [Theory]
    [InlineData(0x708194_0DFBAAUL, true, AddressKind.Resolvable)]
    [InlineData(0xC08194_0DFBAAUL, true, AddressKind.RandomStatic)]
    [InlineData(0x008194_0DFBAAUL, true, AddressKind.NonResolvable)]
    [InlineData(0x708194_0DFBAAUL, false, AddressKind.Public)]
    public void Classify_identifica_el_tipo_de_direccion(ulong address, bool isRandom, AddressKind expected)
        => Assert.Equal(expected, BleAddress.Classify(address, isRandom));

    [Fact]
    public void Format_y_TryParse_son_simetricos()
    {
        const ulong address = 0xAABBCCDDEEFFUL;
        Assert.Equal("AA:BB:CC:DD:EE:FF", BleAddress.Format(address));
        Assert.True(BleAddress.TryParse("AA:BB:CC:DD:EE:FF", out var parsed));
        Assert.Equal(address, parsed);
    }
}

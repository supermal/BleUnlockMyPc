using System.Text;
using AutoUnlockService.Core;
using AutoUnlockService.Credentials;
using Xunit;

namespace AutoUnlockService.Tests;

public class CredentialProtocolTests
{
    [Fact]
    public void BuildCreds_produce_tres_lineas_en_utf8()
    {
        var bytes = CredentialProtocol.BuildCreds("Admin", "clave");

        Assert.Equal("CREDS\nAdmin\nclave\n", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void BuildCreds_conserva_los_caracteres_no_ascii()
    {
        var bytes = CredentialProtocol.BuildCreds("用户", "contraseña");

        Assert.Equal("CREDS\n用户\ncontraseña\n", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void BuildCreds_no_emite_BOM_ni_retorno_de_carro()
    {
        var bytes = CredentialProtocol.BuildCreds("Admin", "clave");

        Assert.Equal((byte)'C', bytes[0]);
        Assert.DoesNotContain((byte)'\r', bytes);
    }

    [Theory]
    [InlineData("Ad\nmin", "clave")]
    [InlineData("Admin", "cla\nve")]
    public void BuildCreds_rechaza_saltos_de_linea_que_romperian_el_encuadre(string user, string password)
    {
        Assert.Throws<ArgumentException>(() => CredentialProtocol.BuildCreds(user, password));
    }

    [Fact]
    public void BuildCreds_no_filtra_ni_usuario_ni_clave_en_la_excepcion()
    {
        // Valores centinela unicos que no coinciden con el texto fijo del mensaje
        var secretUser = "USR-" + Guid.NewGuid().ToString("N");
        var secretPass = "PWD-" + Guid.NewGuid().ToString("N");

        // Fallo por el usuario (la clave es valida) y fallo por la clave (el usuario es valido)
        var byUser = Assert.Throws<ArgumentException>(() => CredentialProtocol.BuildCreds(secretUser + "\n", secretPass));
        var byPass = Assert.Throws<ArgumentException>(() => CredentialProtocol.BuildCreds(secretUser, secretPass + "\n"));

        foreach (var ex in new[] { byUser, byPass })
        {
            // ToString incluye mensaje, nombre del parametro y traza
            var text = ex.ToString();
            Assert.DoesNotContain(secretUser, text);
            Assert.DoesNotContain(secretPass, text);
        }
    }

    [Fact]
    public void TryParseResult_reconoce_el_exito()
    {
        Assert.True(CredentialProtocol.TryParseResult("RESULT OK", out var r));
        Assert.True(r.Success);
        Assert.Equal(0u, r.NtStatus);
    }

    [Fact]
    public void TryParseResult_reconoce_el_fallo_con_su_codigo()
    {
        Assert.True(CredentialProtocol.TryParseResult("RESULT FAIL C000006D", out var r));
        Assert.False(r.Success);
        Assert.Equal(0xC000006Du, r.NtStatus);
    }

    [Fact]
    public void TryParseResult_acepta_hexadecimal_en_minusculas()
    {
        Assert.True(CredentialProtocol.TryParseResult("RESULT FAIL c000006d", out var r));
        Assert.Equal(0xC000006Du, r.NtStatus);
    }

    [Fact]
    public void TryParseResult_acepta_el_formato_exacto_del_cliente_cpp_con_relleno_de_ceros()
    {
        // sprintf("%08X") del CP: siempre 8 digitos, ceros a la izquierda incluidos
        Assert.True(CredentialProtocol.TryParseResult("RESULT FAIL 00000005", out var r));
        Assert.Equal(5u, r.NtStatus);
    }

    [Fact]
    public void TryParseResult_tolera_espacios_y_retorno_de_carro()
    {
        Assert.True(CredentialProtocol.TryParseResult("  RESULT OK\r ", out var r));
        Assert.True(r.Success);
    }

    [Theory]
    [InlineData("ACK")]
    [InlineData("")]
    [InlineData("RESULT")]
    [InlineData("RESULT FAIL")]
    [InlineData("RESULT FAIL no-es-hex")]
    [InlineData("RESULT MAYBE")]
    [InlineData("RESULT OK extra")]
    [InlineData("RESULT FAIL 0xC000006D")]
    [InlineData("RESULT FAIL 1C000006D")]
    [InlineData("RESULT FAIL C000 006D")]
    public void TryParseResult_rechaza_lo_que_no_es_un_resultado(string line)
        => Assert.False(CredentialProtocol.TryParseResult(line, out _));
}

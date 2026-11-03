using System.Security.Principal;
using BleUnlockService.Credentials;
using BleUnlockService.Resources;
using Xunit;

namespace BleUnlockService.Tests;

// Cuentas locales, de dominio y Microsoft (2026-11-03): resolucion del nombre, prevalidacion y
// el subestado que devuelve el CP
public class AccountSupportTests
{
    // —— Protocolo: subestado opcional ——

    [Fact]
    public void El_resultado_trae_el_subestado_del_cp()
    {
        Assert.True(CredentialProtocol.TryParseResult("RESULT FAIL C000006D C000006A", out var r));
        Assert.False(r.Success);
        Assert.Equal(0xC000006Du, r.NtStatus);
        Assert.Equal(0xC000006Au, r.Substatus);
    }

    [Fact]
    public void Un_cp_antiguo_sin_subestado_sigue_entendiendose()
    {
        Assert.True(CredentialProtocol.TryParseResult("RESULT FAIL C000006D", out var r));
        Assert.Equal(0u, r.Substatus);
    }

    [Theory]
    [InlineData("RESULT FAIL C000006D C000006A EXTRA")]
    [InlineData("RESULT FAIL C000006D zzzzzzzz")]
    [InlineData("RESULT FAIL C000006D 6A")]
    public void Un_subestado_mal_formado_invalida_la_linea(string line)
        => Assert.False(CredentialProtocol.TryParseResult(line, out _));

    // —— Descripcion del fallo ——

    [Fact]
    public void El_subestado_explica_la_causa_concreta()
    {
        Assert.Equal(ServiceStrings.Auth_WrongPassword,
            NtStatusPolicy.Describe(NtStatusPolicy.StatusLogonFailure, NtStatusPolicy.StatusWrongPassword));
        Assert.Equal(ServiceStrings.Auth_NoSuchUser,
            NtStatusPolicy.Describe(NtStatusPolicy.StatusLogonFailure, NtStatusPolicy.StatusNoSuchUser));
    }

    [Fact]
    public void Sin_subestado_conocido_se_describe_el_estado_principal()
    {
        Assert.Equal(ServiceStrings.Auth_LogonFailure, NtStatusPolicy.Describe(NtStatusPolicy.StatusLogonFailure));
        Assert.Equal(ServiceStrings.Auth_LogonFailure, NtStatusPolicy.Describe(NtStatusPolicy.StatusLogonFailure, 0x12345678));
        Assert.Equal(ServiceStrings.Auth_AccountDisabled, NtStatusPolicy.Describe(NtStatusPolicy.StatusAccountDisabled));
        Assert.Contains("C0000001", NtStatusPolicy.Describe(0xC0000001));
    }

    // —— Prevalidacion ——

    private static Func<int, int> Attempts(params (int LogonType, int Error)[] results)
    {
        var map = results.ToDictionary(r => r.LogonType, r => r.Error);
        return type => map.TryGetValue(type, out var error) ? error : throw new InvalidOperationException($"tipo {type} no esperado");
    }

    private const int Network = CredentialValidator.Logon32LogonNetwork;
    private const int Interactive = CredentialValidator.Logon32LogonInteractive;

    [Fact]
    public void Clave_correcta_por_red_es_valida()
        => Assert.Equal(CredentialCheck.Valid, CredentialValidator.Decide(Attempts((Network, 0)), false).Check);

    [Fact]
    public void Clave_incorrecta_de_dominio_suspende_sin_segundo_intento()
    {
        var (check, error) = CredentialValidator.Decide(Attempts((Network, 1326)), microsoftAccount: false);
        Assert.Equal(CredentialCheck.Invalid, check);
        Assert.Equal(1326, error);
    }

    [Theory]
    [InlineData(1311)]   // sin controlador de dominio
    [InlineData(1355)]
    [InlineData(1789)]
    public void Sin_contacto_con_el_dominio_no_se_suspende(int error)
        => Assert.Equal(CredentialCheck.Unverified, CredentialValidator.Decide(Attempts((Network, error)), false).Check);

    [Fact]
    public void Si_la_directiva_niega_el_inicio_por_red_se_prueba_el_interactivo()
    {
        Assert.Equal(CredentialCheck.Valid,
            CredentialValidator.Decide(Attempts((Network, 1385), (Interactive, 0)), false).Check);
        Assert.Equal(CredentialCheck.Unverified,
            CredentialValidator.Decide(Attempts((Network, 1385), (Interactive, 1385)), false).Check);
        Assert.Equal(CredentialCheck.Invalid,
            CredentialValidator.Decide(Attempts((Network, 1385), (Interactive, 1326)), false).Check);
    }

    [Fact]
    public void Una_cuenta_microsoft_tiene_un_segundo_intento_interactivo()
    {
        Assert.Equal(CredentialCheck.Valid,
            CredentialValidator.Decide(Attempts((Network, 1326), (Interactive, 0)), microsoftAccount: true).Check);
        Assert.Equal(CredentialCheck.Invalid,
            CredentialValidator.Decide(Attempts((Network, 1326), (Interactive, 1326)), microsoftAccount: true).Check);
    }

    [Theory]
    [InlineData("MicrosoftAccount\\alguien@outlook.com", true)]
    [InlineData("alguien@outlook.com", true)]
    [InlineData("KIROL\\hq.qiu", false)]
    [InlineData("Admin", false)]
    public void Se_reconoce_la_forma_de_una_cuenta_microsoft(string userName, bool expected)
        => Assert.Equal(expected, CredentialValidator.IsMicrosoftAccount(UserNameParser.Split(userName)));

    // —— Resolucion del nombre ——

    [Theory]
    [InlineData(".\\Admin")]
    [InlineData("\\Admin")]
    public void El_punto_y_la_barra_inicial_son_este_equipo(string userName)
        => Assert.Equal(Environment.MachineName + "\\Admin", AccountNameResolver.Normalize(userName));

    [Fact]
    public void Se_usa_el_nombre_canonico_que_devuelve_Windows()
    {
        var resolved = AccountNameResolver.TryResolve("hq.qiu@kirol.es", out var error,
            name => name == "hq.qiu@kirol.es" ? "KIROL\\hq.qiu" : throw new IdentityNotMappedException());

        Assert.Equal("KIROL\\hq.qiu", resolved);
        Assert.Null(error);
    }

    [Fact]
    public void Si_no_se_puede_resolver_se_devuelve_null_con_el_motivo()
    {
        var resolved = AccountNameResolver.TryResolve("KIROL\\hq.qiu", out var error,
            _ => throw new IdentityNotMappedException("sin controlador"));

        Assert.Null(resolved);
        Assert.Equal("sin controlador", error);
    }

    [Fact]
    public void Un_nombre_vacio_no_se_resuelve()
        => Assert.Null(AccountNameResolver.TryResolve("   ", out _, _ => throw new InvalidOperationException()));

    [Fact]
    public void El_usuario_actual_se_resuelve_de_verdad_a_DOMINIO_barra_usuario()
    {
        // Sin dominio delante, tal como lo rellenaria alguien a mano
        var bare = Environment.UserName;
        var resolved = AccountNameResolver.TryResolve(bare, out var error);

        Assert.Null(error);
        Assert.NotNull(resolved);
        Assert.EndsWith("\\" + bare, resolved, StringComparison.OrdinalIgnoreCase);
    }
}

public class AccountNotFoundTests
{
    [Fact]
    public void Una_cuenta_desconocida_para_Windows_se_marca_como_no_encontrada()
    {
        Assert.Null(AccountNameResolver.TryResolve("hq.qiu@outlook.es", out _, out var notFound,
            _ => throw new System.Security.Principal.IdentityNotMappedException()));
        Assert.True(notFound);
    }

    [Fact]
    public void Otro_fallo_de_busqueda_no_cuenta_como_no_encontrada()
    {
        Assert.Null(AccountNameResolver.TryResolve("KIROL\\hq.qiu", out _, out var notFound,
            _ => throw new System.ComponentModel.Win32Exception(1311)));
        Assert.False(notFound);
    }

    [Fact]
    public void El_motivo_concreto_llega_a_la_suspension()
    {
        var sm = new BleUnlockService.Core.ProximityStateMachine(TimeProvider.System,
            new BleUnlockService.Config.ProximityOptions(), new BleUnlockService.Config.NotificationOptions(), false);

        var change = sm.OnCredentialsChanged(false, "sin cuenta");

        Assert.Equal(BleUnlockService.Core.ProximityState.Suspended, change?.To);
        Assert.Equal("sin cuenta", change!.Reason);
    }
}

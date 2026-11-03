using BleUnlockManager.Core.Config;
using Xunit;

namespace BleUnlockManager.Tests;

/// <summary>Aviso al guardar una cuenta que no puede desbloquear la sesión actual (2026-11-03).</summary>
public sealed class AccountCheckTests
{
    private static ConfigChange Change(string field) => new(field, field, null, null, false);

    [Theory]
    [InlineData(AccountMatchKind.Same)]
    [InlineData(AccountMatchKind.Unknown)]
    public void La_cuenta_actual_o_sin_comprobar_no_avisa(AccountMatchKind kind)
        => Assert.Null(AccountCheck.Warning("ORD469\\Admin", new AccountMatch(kind, "ORD469\\Admin")));

    [Theory]
    [InlineData(AccountMatchKind.NotFound)]
    [InlineData(AccountMatchKind.Other)]
    public void Otra_cuenta_o_inexistente_avisa_con_ambos_nombres(AccountMatchKind kind)
    {
        var warning = AccountCheck.Warning(" hq.qiu@outlook.es ", new AccountMatch(kind, "ORD469\\Admin"));

        Assert.NotNull(warning);
        Assert.Contains("hq.qiu@outlook.es", warning);
        Assert.Contains("ORD469\\Admin", warning);
    }

    [Fact]
    public void Los_dos_avisos_son_distintos()
        => Assert.NotEqual(
            AccountCheck.Warning("x", new AccountMatch(AccountMatchKind.NotFound, "y")),
            AccountCheck.Warning("x", new AccountMatch(AccountMatchKind.Other, "y")));

    [Fact]
    public void Solo_se_comprueba_si_cambian_usuario_o_clave()
    {
        Assert.True(AccountCheck.ShouldCheck("Admin", [Change(ConfigFields.CredentialsUserName)]));
        Assert.True(AccountCheck.ShouldCheck("Admin", [Change(ConfigFields.CredentialsPassword)]));
        Assert.False(AccountCheck.ShouldCheck("Admin", [Change("Proximity.LockThresholdDbm")]));
        Assert.False(AccountCheck.ShouldCheck("  ", [Change(ConfigFields.CredentialsUserName)]));
    }
}

using BleUnlockService.Credentials;
using Xunit;

namespace BleUnlockService.Tests;

public class NtStatusPolicyTests
{
    [Theory]
    [InlineData(0xC000006Du)]   // STATUS_LOGON_FAILURE
    [InlineData(0xC0000071u)]   // STATUS_PASSWORD_EXPIRED
    [InlineData(0xC0000224u)]   // STATUS_PASSWORD_MUST_CHANGE
    [InlineData(0xC0000234u)]   // STATUS_ACCOUNT_LOCKED_OUT
    public void ShouldSuspend_es_cierto_para_fallos_de_credencial(uint status)
        => Assert.True(NtStatusPolicy.ShouldSuspend(status));

    [Theory]
    [InlineData(0x00000000u)]   // STATUS_SUCCESS
    [InlineData(0xC000015Bu)]   // STATUS_LOGON_TYPE_NOT_GRANTED
    [InlineData(0xC0000022u)]   // STATUS_ACCESS_DENIED
    [InlineData(0xDEADBEEFu)]   // desconocido
    public void ShouldSuspend_es_falso_para_el_resto(uint status)
        => Assert.False(NtStatusPolicy.ShouldSuspend(status));

    [Fact]
    public void Describe_da_un_texto_en_chino_para_los_codigos_conocidos()
        => Assert.Contains("密码", NtStatusPolicy.Describe(0xC000006Du));

    [Fact]
    public void Describe_da_el_codigo_hexadecimal_para_los_desconocidos()
        => Assert.Contains("DEADBEEF", NtStatusPolicy.Describe(0xDEADBEEFu));
}

using BleUnlockManager.Core.Bonds;
using BleUnlockManager.Core.Config;
using BleUnlockManager.Core.Management;
using BleUnlockManager.Core.Telemetry;
using Xunit;

namespace BleUnlockManager.Tests;

/// <summary>Equipo sin configurar (2026-11-02): qué falta, cómo se ve en la bandeja y en la lista de emparejados.</summary>
public sealed class SetupStateTests
{
    private const string Irk = "00112233445566778899AABBCCDDEEFF";

    private static BleUnlockConfig Config(string irk = Irk, string user = "test", string password = "pw")
    {
        var config = new BleUnlockConfig();
        config.Device.Irk = irk;
        config.Credentials.UserName = user;
        config.Credentials.Password = password;
        return config;
    }

    private static SnapshotFrame Snapshot(string? setup, string state = "Unlocked", SnapshotBatch? batch = null) =>
        new("snapshot", 1, 1, 1, 10_000, 60,
            new SnapshotState(state, 0),
            new SnapshotScan("", null, "running"),
            new SnapshotConnection(true),
            new SnapshotConnection(false),
            new SnapshotThresholds(-80, -45, 3, 5, 10),
            new SnapshotAverages(null, null, 0, 0),
            null,
            [],
            batch,
            setup);

    // —— SetupCheck (espejo de ConfigCompleteness del servicio) ——

    [Fact]
    public void FreshInstall_HasNoDevice()
        => Assert.Equal(SetupState.NoDevice, SetupCheck.Evaluate(new BleUnlockConfig()));

    [Fact]
    public void PlaceholderIrk_CountsAsNoDevice()
        => Assert.Equal(SetupState.NoDevice, SetupCheck.Evaluate(Config(irk: ConfigValidator.NoDeviceIrk)));

    [Fact]
    public void IdentityAddressAlone_CountsAsDevice()
    {
        var config = Config(irk: "");
        config.Device.IdentityAddress = "AA:BB:CC:DD:EE:FF";
        Assert.Equal(SetupState.Complete, SetupCheck.Evaluate(config));
    }

    [Theory]
    [InlineData("", "pw")]
    [InlineData("test", "")]
    public void MissingUserOrPassword_IsNoCredentials(string user, string password)
        => Assert.Equal(SetupState.NoCredentials, SetupCheck.Evaluate(Config(user: user, password: password)));

    [Fact]
    public void Everything_IsComplete()
        => Assert.Equal(SetupState.Complete, SetupCheck.Evaluate(Config()));

    [Theory]
    [InlineData("noDevice", SetupState.NoDevice)]
    [InlineData("noCredentials", SetupState.NoCredentials)]
    [InlineData(null, SetupState.Complete)]
    [InlineData("algoNuevo", SetupState.Complete)]
    public void FromWire_MapsServiceCodes(string? code, SetupState expected)
        => Assert.Equal(expected, SetupCheck.FromWire(code));

    // —— Bandeja ——

    [Fact]
    public void NoDevice_ShowsNeedsSetup_EvenWhileSuspended()
        => Assert.Equal(TrayState.NeedsSetup,
            TrayStates.Resolve(true, true, "Suspended", null, SetupState.NoDevice));

    [Fact]
    public void NoCredentials_ShowsNeedsSetupAtRest_ButLeavingWins()
    {
        Assert.Equal(TrayState.NeedsSetup, TrayStates.Resolve(true, true, "Unlocked", null, SetupState.NoCredentials));
        Assert.Equal(TrayState.Leaving, TrayStates.Resolve(true, true, "PendingLock", null, SetupState.NoCredentials));
    }

    [Fact]
    public void ServiceStopped_StillWinsOverSetup()
        => Assert.Equal(TrayState.Stopped, TrayStates.Resolve(false, false, null, null, SetupState.NoDevice));

    [Fact]
    public void NeedsSetup_TooltipExplainsWhatIsMissing()
    {
        var noDevice = TrayStates.Tooltip(TrayState.NeedsSetup, "Unlocked", "—", null, -80, null, null, SetupState.NoDevice);
        var noPassword = TrayStates.Tooltip(TrayState.NeedsSetup, "Unlocked", "—", null, -80, null, null, SetupState.NoCredentials);

        Assert.NotEqual(noDevice.Line2, noPassword.Line2);
        Assert.True(noDevice.ToString().Length <= TrayTooltip.MaxLength);
    }

    [Fact]
    public void TelemetryModel_ReadsSetupFromSnapshot_OnlyWhileConnected()
    {
        var model = new TelemetryModel { ServiceRunning = true };
        model.Apply(Snapshot("noDevice"));

        Assert.Equal(SetupState.NoDevice, model.Setup);
        Assert.Equal(TrayState.NeedsSetup, model.TrayState);

        model.SetConnected(false);
        Assert.Equal(SetupState.Complete, model.Setup);
    }

    [Fact]
    public void FrameParser_ReadsSetupField_AndOlderServicesWithoutIt()
    {
        var withSetup = ManagementFrameParser.Parse(
            """{"t":"snapshot","v":1,"seq":1,"gen":1,"tsMs":1,"uptimeSec":1,"samples":[],"setup":"noCredentials"}""");
        var without = ManagementFrameParser.Parse(
            """{"t":"snapshot","v":1,"seq":1,"gen":1,"tsMs":1,"uptimeSec":1,"samples":[]}""");

        Assert.Equal("noCredentials", Assert.IsType<SnapshotFrame>(withSetup).Setup);
        Assert.Null(Assert.IsType<SnapshotFrame>(without).Setup);
    }

    // —— Lista de emparejados ——

    [Fact]
    public void BondWithoutIrk_IsListedButNotSupported()
    {
        Assert.False(new BleBondDevice("AA:BB:CC:DD:EE:FF", "Pulsera", "").IsSupported);
        Assert.True(new BleBondDevice("AA:BB:CC:DD:EE:FF", "Watch", Irk).IsSupported);
    }
}

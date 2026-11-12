using System.Text.Json.Nodes;
using BleUnlock.Shared;
using BleUnlockManager.Core;
using BleUnlockManager.Core.Config;
using BleUnlockManager.Core.Localization;
using BleUnlockManager.Core.Management;
using BleUnlockManager.Core.Rules;
using BleUnlockManager.Core.Telemetry;
using Xunit;

namespace BleUnlockManager.Tests;

// Gestor: protección contra cambios bruscos (configuración, progreso, reglas) y arranque automático del servicio
[Collection(TestCulture.LanguageCollection)]
public sealed class SuddenGuardManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bleunlock-guard-tests", Guid.NewGuid().ToString("N"));

    public SuddenGuardManagerTests()
    {
        Directory.CreateDirectory(_dir);
        ManagerLanguage.Set(AppLanguage.Chinese);
    }

    public void Dispose()
    {
        ManagerLanguage.Set(AppLanguage.Chinese);
        TestCulture.Reset();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Los_ajustes_se_guardan_y_se_vuelven_a_leer()
    {
        var path = Path.Combine(_dir, "appsettings.json");
        File.WriteAllText(path, """{ "BleUnlock": { "Proximity": { "LockThresholdDbm": -70 } } }""");
        var config = ConfigDocument.Load(path);
        Assert.Equal(25, config.Proximity.SuddenChangeReferenceCount);   // sin clave: predeterminado
        config.Proximity.SuddenChangeReferenceCount = 30;
        config.Proximity.SuddenChangeDeltaDb = 15;
        config.Proximity.SuddenDropConfirmSeconds = 45;
        config.Proximity.SuddenRiseConfirmSeconds = 8;

        ConfigDocument.Save(path, config);

        var proximity = JsonNode.Parse(File.ReadAllText(path))!["BleUnlock"]!["Proximity"]!;
        Assert.Equal(30, proximity["SuddenChangeReferenceCount"]!.GetValue<int>());
        var reloaded = ConfigDocument.Load(path).Proximity;
        Assert.Equal(15, reloaded.SuddenChangeDeltaDb);
        Assert.Equal(45, reloaded.SuddenDropConfirmSeconds);
        Assert.Equal(8, reloaded.SuddenRiseConfirmSeconds);
    }

    [Theory]
    [InlineData(25, 20, 30, 5, true)]
    [InlineData(25, 0, 30, 5, true)]
    [InlineData(4, 20, 30, 5, false)]
    [InlineData(25, 61, 30, 5, false)]
    [InlineData(25, 20, 301, 5, false)]
    [InlineData(25, 20, 30, 0, false)]
    public void El_validador_espejo_aplica_los_mismos_limites(int count, double delta, double drop, double rise, bool valid)
    {
        var config = new BleUnlockConfig();
        config.Proximity.SuddenChangeReferenceCount = count;
        config.Proximity.SuddenChangeDeltaDb = delta;
        config.Proximity.SuddenDropConfirmSeconds = drop;
        config.Proximity.SuddenRiseConfirmSeconds = rise;

        Assert.Equal(valid, ConfigValidator.Validate(config).IsValid);
    }

    [Fact]
    public void Los_cambios_aparecen_en_el_resumen()
    {
        var from = new BleUnlockConfig();
        var to = from.Clone();
        to.Proximity.SuddenDropConfirmSeconds = 40;

        var change = Assert.Single(ConfigDiff.Compute(from, to));
        Assert.Equal("突降确认时长（秒）", change.DisplayName);
    }

    [Fact]
    public void La_observacion_se_muestra_en_segundos()
    {
        Assert.Equal("12/30 秒", BatchText.Progress(new SnapshotBatch("suddenDrop", 12, 30)));
        Assert.Equal("2/5 秒", BatchText.Progress(new SnapshotBatch("suddenRise", 2, 5)));
        Assert.Equal("3/5", BatchText.Progress(new SnapshotBatch("leave", 3, 5)));
    }

    [Fact]
    public void La_caida_brusca_cuenta_como_saliendo_en_la_bandeja()
        => Assert.Equal(TrayState.Leaving, TrayStates.Resolve(true, true, "Unlocked", new SnapshotBatch("suddenDrop", 3, 30)));

    [Fact]
    public void La_pagina_de_reglas_describe_la_proteccion_con_sus_valores()
    {
        var lead = RulesContent.Build(RuleValues.Defaults with { SuddenDelta = 15, SuddenDropSeconds = 40 })[0].Lead!;
        Assert.Contains("≥ 15 dB", lead);
        Assert.Contains("观察 40 秒", lead);

        var off = RulesContent.Build(RuleValues.Defaults with { SuddenDelta = 0 })[0].Lead!;
        Assert.Contains("突变保护已关闭", off);
    }

    // —— Arranque automático del servicio ——

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]    // lo detuvo el usuario
    [InlineData(false, false, false)]  // ya en marcha, arrancando o no instalado
    public void Solo_se_arranca_un_servicio_parado_que_nadie_detuvo(bool stopped, bool byUser, bool expected)
        => Assert.Equal(expected, ServiceAutoStart.ShouldStart(stopped, byUser));

    [Fact]
    public void Detener_se_recuerda_y_arrancar_lo_olvida()
    {
        var marker = Path.Combine(_dir, "sub", "service-stopped-by-user.txt");
        Assert.False(ServiceAutoStart.StoppedByUser(marker));

        ServiceAutoStart.RememberStoppedByUser(marker, stopped: true);
        Assert.True(ServiceAutoStart.StoppedByUser(marker));

        ServiceAutoStart.RememberStoppedByUser(marker, stopped: false);
        Assert.False(ServiceAutoStart.StoppedByUser(marker));
    }
}

using System.Text.Json;
using AutoUnlockService.Config;
using AutoUnlockService.Core;
using AutoUnlockService.Management;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AutoUnlockService.Tests.Management;

// 遥测中枢：环形裁剪、回溯/增量、窗口均值、序号与状态语义
public class TelemetryHubTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static TelemetryHub Create(out FakeTimeProvider clock, OptionsGate? gate = null)
    {
        clock = new FakeTimeProvider();
        clock.SetUtcNow(T0);
        gate ??= TestGates.New(clock);
        return new TelemetryHub(gate, clock, NullLogger<TelemetryHub>.Instance);
    }

    private static long Ms(DateTimeOffset at) => at.ToUnixTimeMilliseconds();

    private static SnapshotFrame FirstSnapshot(TelemetryHub hub)
    {
        hub.BeginClientSession();
        return JsonSerializer.Deserialize<SnapshotFrame>(hub.BuildSnapshot(), ManagementProtocol.JsonOptions)!;
    }

    [Fact]
    public void Sin_muestras_la_retrospectiva_esta_vacia_y_las_medias_son_null()
    {
        var hub = Create(out _);

        var frame = FirstSnapshot(hub);

        Assert.Empty(frame.Samples);
        Assert.Null(frame.Avg.W5);
        Assert.Null(frame.Avg.W10);
        Assert.Equal(0, frame.Avg.N5);
        Assert.Equal(0, frame.Avg.N10);
        Assert.Null(frame.State.Name);
        Assert.Null(frame.LastMatchMs);
    }

    [Fact]
    public void La_primera_toma_es_retrospectiva_y_las_siguientes_incrementales()
    {
        var hub = Create(out var clock);
        hub.OnSample(Ms(T0.AddSeconds(-2)), -61);
        hub.OnSample(Ms(T0.AddSeconds(-1)), -59);

        var first = FirstSnapshot(hub);
        Assert.Equal(2, first.Samples.Count);
        Assert.Equal(Ms(T0.AddSeconds(-2)), first.Samples[0][0]);
        Assert.Equal(-61, first.Samples[0][1]);
        Assert.Equal(-59, first.Samples[1][1]);

        var second = JsonSerializer.Deserialize<SnapshotFrame>(
            hub.BuildSnapshot(), ManagementProtocol.JsonOptions)!;
        Assert.Empty(second.Samples);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        hub.OnSample(Ms(clock.GetUtcNow()), -50);
        var third = JsonSerializer.Deserialize<SnapshotFrame>(
            hub.BuildSnapshot(), ManagementProtocol.JsonOptions)!;
        Assert.Single(third.Samples);
        Assert.Equal(-50, third.Samples[0][1]);
    }

    [Fact]
    public void Reabrir_sesion_vuelve_a_entregar_la_retrospectiva_completa()
    {
        var hub = Create(out _);
        hub.OnSample(Ms(T0.AddSeconds(-3)), -61);
        hub.OnSample(Ms(T0.AddSeconds(-2)), -59);

        Assert.Equal(2, FirstSnapshot(hub).Samples.Count);

        // 模拟管理端重连：新会话必须再次拿到回溯，而不是空增量
        var again = FirstSnapshot(hub);
        Assert.Equal(2, again.Samples.Count);
    }

    [Fact]
    public void El_limite_de_4096_descarta_lo_mas_antiguo()
    {
        var hub = Create(out _);
        for (int i = 0; i < 5000; i++) hub.OnSample(Ms(T0.AddMilliseconds(-5000 + i)), (short)(-50 - i % 10));

        var frame = FirstSnapshot(hub);

        Assert.Equal(TelemetryHub.SampleCapacity, frame.Samples.Count);
        // 保留下来的第一条应是第 904 条（5000 - 4096），其时间戳为 T0-4096ms
        Assert.Equal(Ms(T0.AddMilliseconds(-4096)), frame.Samples[0][0]);
        Assert.Equal(Ms(T0.AddMilliseconds(-1)), frame.Samples[^1][0]);
    }

    [Fact]
    public void Las_muestras_mas_viejas_que_5_minutos_no_entran_en_la_retrospectiva()
    {
        var hub = Create(out _);
        hub.OnSample(Ms(T0.AddSeconds(-400)), -80);
        hub.OnSample(Ms(T0.AddSeconds(-1)), -60);

        var frame = FirstSnapshot(hub);

        Assert.Single(frame.Samples);
        Assert.Equal(-60, frame.Samples[0][1]);
    }

    [Fact]
    public void Las_medias_w5_w10_usan_las_ventanas_de_5_y_10_segundos()
    {
        var hub = Create(out _);
        hub.OnSample(Ms(T0.AddSeconds(-1)), -40);
        hub.OnSample(Ms(T0.AddSeconds(-2)), -50);
        hub.OnSample(Ms(T0.AddSeconds(-6)), -60);

        var frame = FirstSnapshot(hub);

        Assert.Equal(2, frame.Avg.N5);
        Assert.Equal(-45, frame.Avg.W5!.Value, 6);
        Assert.Equal(3, frame.Avg.N10);
        Assert.Equal(-50, frame.Avg.W10!.Value, 6);
    }

    [Fact]
    public void El_seq_es_estrictamente_creciente()
    {
        var hub = Create(out _);
        hub.BeginClientSession();

        var a = JsonSerializer.Deserialize<SnapshotFrame>(hub.BuildSnapshot(), ManagementProtocol.JsonOptions)!;
        var b = JsonSerializer.Deserialize<SnapshotFrame>(hub.BuildSnapshot(), ManagementProtocol.JsonOptions)!;
        var c = JsonSerializer.Deserialize<SnapshotFrame>(hub.BuildSnapshot(), ManagementProtocol.JsonOptions)!;

        Assert.Equal(a.Seq + 1, b.Seq);
        Assert.Equal(b.Seq + 1, c.Seq);
    }

    [Fact]
    public void El_cambio_de_estado_actualiza_nombre_y_sinceMs()
    {
        var hub = Create(out var clock);
        hub.OnStateChange(ProximityState.Unlocked);
        clock.Advance(TimeSpan.FromSeconds(5));
        hub.OnStateChange(ProximityState.PendingLock);

        var frame = FirstSnapshot(hub);
        Assert.Equal("PendingLock", frame.State.Name);
        Assert.Equal(Ms(T0.AddSeconds(5)), frame.State.SinceMs);

        // 同名重复调用是幂等的：sinceMs 保留首次进入的时间
        clock.Advance(TimeSpan.FromSeconds(3));
        hub.OnStateChange(ProximityState.PendingLock);
        var later = JsonSerializer.Deserialize<SnapshotFrame>(hub.BuildSnapshot(), ManagementProtocol.JsonOptions)!;
        Assert.Equal(Ms(T0.AddSeconds(5)), later.State.SinceMs);
    }

    [Fact]
    public void LastMatchMs_es_null_hasta_la_primera_muestra_y_luego_mide_el_tiempo()
    {
        var hub = Create(out var clock);
        Assert.Null(FirstSnapshot(hub).LastMatchMs);

        hub.OnSample(Ms(clock.GetUtcNow()), -60);
        clock.Advance(TimeSpan.FromSeconds(2));

        var frame = JsonSerializer.Deserialize<SnapshotFrame>(hub.BuildSnapshot(), ManagementProtocol.JsonOptions)!;
        Assert.Equal(2000, frame.LastMatchMs);
    }

    [Fact]
    public void UptimeSec_sigue_al_reloj()
    {
        var hub = Create(out var clock);
        Assert.Equal(0, FirstSnapshot(hub).UptimeSec, 6);

        clock.Advance(TimeSpan.FromSeconds(90.5));
        var frame = JsonSerializer.Deserialize<SnapshotFrame>(hub.BuildSnapshot(), ManagementProtocol.JsonOptions)!;
        Assert.Equal(90.5, frame.UptimeSec, 3);
    }

    [Fact]
    public void Los_umbrales_siguen_a_la_configuracion_vigente_tras_el_antirrebote()
    {
        var clock = new FakeTimeProvider();
        clock.SetUtcNow(T0);
        var gate = TestGates.New(clock);
        var hub = new TelemetryHub(gate, clock, NullLogger<TelemetryHub>.Instance);

        var before = FirstSnapshot(hub);
        Assert.Equal(gate.Current.Proximity.LockThresholdDbm, before.Thr.LockDbm);
        Assert.Equal(gate.Current.Proximity.UnlockWindowSeconds, before.Thr.UnlockWinSec);

        // 合法候选：防抖期满（与生产相同的 250ms）后生效，下一帧立即反映
        var candidate = TestGates.ValidOptions();
        candidate.Proximity = new ProximityOptions
        {
            LockThresholdDbm = -70,
            UnlockThresholdDbm = -40,
            LockWindowSeconds = 20,
            UnlockWindowSeconds = 7,
            MinSamples = 5
        };
        gate.Submit(candidate);
        clock.Advance(TestGates.Debounce);

        var after = JsonSerializer.Deserialize<SnapshotFrame>(hub.BuildSnapshot(), ManagementProtocol.JsonOptions)!;
        Assert.Equal(-70, after.Thr.LockDbm);
        Assert.Equal(-40, after.Thr.UnlockDbm);
        Assert.Equal(20, after.Thr.LockWinSec);
        Assert.Equal(7, after.Thr.UnlockWinSec);
        Assert.Equal(5, after.Thr.MinSamples);
    }

    [Fact]
    public void Una_configuracion_invalida_no_cambia_los_umbrales_de_la_toma()
    {
        var clock = new FakeTimeProvider();
        clock.SetUtcNow(T0);
        var gate = TestGates.New(clock);
        var hub = new TelemetryHub(gate, clock, NullLogger<TelemetryHub>.Instance);
        var before = FirstSnapshot(hub);

        var bad = TestGates.ValidOptions();
        bad.Device.Irk = "no-es-hexadecimal";
        bad.Proximity.LockThresholdDbm = -30;   // 若被误采纳，阈值会变
        gate.Submit(bad);
        clock.Advance(TimeSpan.FromSeconds(1));

        var after = JsonSerializer.Deserialize<SnapshotFrame>(hub.BuildSnapshot(), ManagementProtocol.JsonOptions)!;
        Assert.Equal(before.Thr.LockDbm, after.Thr.LockDbm);
        Assert.Equal(before.Thr.UnlockDbm, after.Thr.UnlockDbm);
        Assert.Equal(before.Thr.MinSamples, after.Thr.MinSamples);
    }

    [Fact]
    public void El_estado_de_conexion_de_ayudante_y_credenciales_aparece_en_la_toma()
    {
        var hub = Create(out _);
        var frame = FirstSnapshot(hub);
        Assert.False(frame.Helper.Connected);
        Assert.False(frame.Cred.Connected);

        hub.OnHelperConnection(true);
        hub.OnCredConnection(true);

        var next = JsonSerializer.Deserialize<SnapshotFrame>(hub.BuildSnapshot(), ManagementProtocol.JsonOptions)!;
        Assert.True(next.Helper.Connected);
        Assert.True(next.Cred.Connected);
    }

    [Fact]
    public void El_estado_de_escaneo_se_pasa_tal_cual_con_detail_null()
    {
        var hub = Create(out _);
        hub.OnScanStatus("未找到支持 BLE 的蓝牙适配器");

        var frame = FirstSnapshot(hub);
        Assert.Equal("未找到支持 BLE 的蓝牙适配器", frame.Scan.Status);
        Assert.Null(frame.Scan.Detail);
    }

    [Fact]
    public void Changed_solo_se_dispara_por_estado_escaneo_y_conexion_no_por_muestras()
    {
        var hub = Create(out _);
        int raised = 0;
        hub.Changed += () => raised++;

        hub.OnSample(Ms(T0), -60);
        Assert.Equal(0, raised);

        hub.OnScanStatus("BLE 扫描已启动");
        Assert.Equal(1, raised);
        hub.OnScanStatus("BLE 扫描已启动");   // 同值不重复触发
        Assert.Equal(1, raised);

        hub.OnStateChange(ProximityState.Unlocked);
        Assert.Equal(2, raised);

        hub.OnHelperConnection(true);
        Assert.Equal(3, raised);
        hub.OnHelperConnection(true);
        Assert.Equal(3, raised);

        hub.OnCredConnection(true);
        Assert.Equal(4, raised);
    }

    [Fact]
    public void Un_suscriptor_que_falla_no_afecta_a_la_ingesta()
    {
        var hub = Create(out _);
        hub.Changed += () => throw new InvalidOperationException("fallo del suscriptor");

        hub.OnScanStatus("BLE 扫描已启动");   // 不得向外抛异常

        var frame = FirstSnapshot(hub);
        Assert.Equal("BLE 扫描已启动", frame.Scan.Status);
    }

    [Fact]
    public void OnHistoryReset_limpia_retrospectiva_y_medias_y_avanza_gen()
    {
        var hub = Create(out _);
        hub.OnSample(Ms(T0.AddSeconds(-1)), -40);

        var before = FirstSnapshot(hub);
        Assert.Equal(1, before.Gen);
        Assert.Single(before.Samples);
        Assert.NotNull(before.Avg.W5);

        hub.OnHistoryReset();

        var after = JsonSerializer.Deserialize<SnapshotFrame>(hub.BuildSnapshot(), ManagementProtocol.JsonOptions)!;
        Assert.Equal(2, after.Gen);
        Assert.Empty(after.Samples);
        Assert.Null(after.Avg.W5);
        Assert.Null(after.Avg.W10);
        Assert.Equal(0, after.Avg.N5);
        Assert.Equal(0, after.Avg.N10);

        // 重置后新样本重新累积：回溯为空，增量从新样本开始
        hub.OnSample(Ms(T0.AddMilliseconds(100)), -55);
        var next = JsonSerializer.Deserialize<SnapshotFrame>(hub.BuildSnapshot(), ManagementProtocol.JsonOptions)!;
        Assert.Equal(2, next.Gen);
        Assert.Single(next.Samples);
        Assert.Equal(-55, next.Samples[0][1]);
    }

    [Fact]
    public void OnHistoryReset_dispara_Changed_para_la_presentacion_inmediata()
    {
        var hub = Create(out _);
        int raised = 0;
        hub.Changed += () => raised++;

        hub.OnHistoryReset();

        Assert.Equal(1, raised);
    }

    [Fact]
    public void La_toma_no_contiene_credenciales_ni_IRK()
    {
        const string user = "USUARIO-SENTINELA";
        const string pass = "CLAVE-SENTINELA";
        const string irk = "IRK-SENTINELA-9f2c";
        var options = TestGates.ValidOptions();
        options.Credentials = new CredentialOptions { UserName = user, Password = pass };
        options.Device = new DeviceOptions { Irk = irk };

        var clock = new FakeTimeProvider();
        clock.SetUtcNow(T0);
        var hub = new TelemetryHub(TestGates.New(clock, options), clock, NullLogger<TelemetryHub>.Instance);
        hub.OnSample(Ms(T0), -50);

        var line = hub.BuildSnapshot();

        Assert.DoesNotContain(user, line);
        Assert.DoesNotContain(pass, line);
        Assert.DoesNotContain(irk, line);
        Assert.DoesNotContain(ManagementProtocol.SerializeLine(ManagementProtocol.CreateHello("1.0.0.0")), user);
    }
}

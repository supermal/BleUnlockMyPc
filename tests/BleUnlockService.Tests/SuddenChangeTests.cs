using BleUnlockService.Config;
using BleUnlockService.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BleUnlockService.Tests;

// Protección contra cambios bruscos (registro del 2026-11-10): una lectura que salta de golpe respecto a las
// 25 lecturas reales anteriores no se cree enseguida; se observa y solo cuenta si la señal se queda ahí
public class SuddenChangeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 8, 55, 0, TimeSpan.FromHours(1));

    // Los umbrales del usuario ese día: bloqueo -70, desbloqueo -50
    private static ProximityOptions Options(double delta = 20) => new()
    {
        LockThresholdDbm = -70,
        UnlockThresholdDbm = -50,
        SuddenChangeDeltaDb = delta,
    };

    private sealed record Rig(ProximityStateMachine Sm, FakeTimeProvider Time, RssiHistory History)
    {
        public DateTimeOffset Now => Time.GetUtcNow();

        public void Real(short rssi) => History.Add(rssi, Now, real: true);

        public void Copy(short rssi) => History.Add(rssi, Now, real: false);

        public StateChange? Step(TimeSpan by)
        {
            Time.Advance(by);
            return Sm.Evaluate(History);
        }
    }

    // Desbloqueado tras la gracia, con 25 lecturas reales estables alrededor de -40 (usuario sentado)
    private static Rig Sitting(ProximityOptions? options = null, short level = -40)
    {
        var time = new FakeTimeProvider(T0);
        var history = new RssiHistory(TimeSpan.FromSeconds(120), time);
        var sm = new ProximityStateMachine(time, options ?? Options(), new NotificationOptions(), false);
        var rig = new Rig(sm, time, history);
        time.Advance(TimeSpan.FromSeconds(20));
        for (var i = 0; i < 25; i++)
        {
            rig.Real((short)(level + (i % 3)));
            Assert.Null(rig.Step(TimeSpan.FromMilliseconds(500)));
        }

        Assert.Equal(ProximityState.Unlocked, sm.State);
        return rig;
    }

    // —— Caída brusca ——

    [Fact]
    public void Una_caida_brusca_repetida_por_el_muestreo_no_avisa_en_tres_segundos()
    {
        // 08:55 del registro: un −87 suelto, repetido cada 300 ms, avisaba y bloqueaba en unos 3 s
        var rig = Sitting();
        rig.Time.Advance(TimeSpan.FromMilliseconds(300));
        rig.Real(-87);
        for (var i = 0; i < 20; i++)
        {
            rig.Time.Advance(TimeSpan.FromMilliseconds(300));
            rig.Copy(-87);
            Assert.Null(rig.Sm.Evaluate(rig.History));
        }

        Assert.Equal(ProximityState.Unlocked, rig.Sm.State);
        Assert.Contains(rig.Sm.DrainNotes(), n => n.StartsWith("信号突降"));
    }

    [Fact]
    public void Si_la_senal_vuelve_se_anula_la_observacion_sin_aviso()
    {
        var rig = Sitting();
        rig.Real(-87);
        Assert.Null(rig.Step(TimeSpan.FromSeconds(1)));
        rig.Real(-41);
        Assert.Null(rig.Step(TimeSpan.FromSeconds(1)));

        var notes = rig.Sm.DrainNotes();
        Assert.Contains(notes, n => n.StartsWith("突降观察取消"));
        Assert.Null(rig.Sm.Progress);

        // Pasados los 30 s no ocurre nada: la observación ya no existe
        for (var i = 0; i < 40; i++)
        {
            rig.Real(-41);
            Assert.Null(rig.Step(TimeSpan.FromSeconds(1)));
        }
    }

    [Fact]
    public void Treinta_segundos_por_debajo_con_tres_lecturas_reales_bloquea_sin_aviso()
    {
        var rig = Sitting();
        var start = rig.Now;
        rig.Real(-87);
        StateChange? change = null;
        for (var i = 0; i < 40 && change is null; i++)
        {
            if (i % 10 == 9) rig.Real(-85);  // el reloj anuncia cada 10 s
            else rig.Copy(-87);
            change = rig.Step(TimeSpan.FromSeconds(1));
        }

        Assert.NotNull(change);
        Assert.Equal(ProximityAction.LockScreen, change!.Action);          // directo, sin ShowLockPrompt
        Assert.Equal(ProximityState.LockedWaitingDeparture, change.To);
        Assert.Contains("直接锁屏", change.Reason);
        Assert.Equal(TimeSpan.FromSeconds(30), rig.Now - start);
    }

    [Fact]
    public void Con_solo_dos_lecturas_reales_espera_a_la_tercera()
    {
        var rig = Sitting();
        var start = rig.Now;
        rig.Real(-87);
        Assert.Null(rig.Step(TimeSpan.FromSeconds(15)));
        rig.Real(-86);
        Assert.Null(rig.Step(TimeSpan.FromSeconds(16)));     // 31 s, solo 2 reales
        Assert.Equal(ProximityState.Unlocked, rig.Sm.State);

        rig.Real(-88);
        var change = rig.Step(TimeSpan.FromSeconds(4));
        Assert.Equal(ProximityAction.LockScreen, change?.Action);
        Assert.Equal(TimeSpan.FromSeconds(35), rig.Now - start);
    }

    [Fact]
    public void Una_bajada_gradual_sigue_el_flujo_normal_con_aviso()
    {
        // Alejarse: la señal baja poco a poco y la media de las 25 anteriores ya está cerca del umbral
        var rig = Sitting(level: -40);
        foreach (short rssi in new short[] { -45, -50, -55, -58, -60, -63, -65, -66, -67, -68, -69, -68, -69, -69, -68, -69, -69, -69, -68, -69 })
        {
            rig.Real(rssi);
            Assert.Null(rig.Step(TimeSpan.FromMilliseconds(500)));
        }

        StateChange? change = null;
        for (var i = 0; i < 10 && change is null; i++)
        {
            rig.Real(-72);
            change = rig.Step(TimeSpan.FromMilliseconds(500));
        }

        Assert.Equal(ProximityAction.ShowLockPrompt, change?.Action);
        Assert.DoesNotContain(rig.Sm.DrainNotes(), n => n.StartsWith("信号突降"));
    }

    [Fact]
    public void Con_menos_de_cinco_referencias_no_se_juzga()
    {
        var time = new FakeTimeProvider(T0);
        var history = new RssiHistory(TimeSpan.FromSeconds(120), time);
        var sm = new ProximityStateMachine(time, Options(), new NotificationOptions(), false);
        time.Advance(TimeSpan.FromSeconds(20));
        for (var i = 0; i < 4; i++)
        {
            history.Add(-40, time.GetUtcNow());
            time.Advance(TimeSpan.FromMilliseconds(500));
        }

        StateChange? change = null;
        for (var i = 0; i < 10 && change is null; i++)
        {
            history.Add(-87, time.GetUtcNow());
            time.Advance(TimeSpan.FromMilliseconds(500));
            change = sm.Evaluate(history);
        }

        Assert.Equal(ProximityAction.ShowLockPrompt, change?.Action);
    }

    [Fact]
    public void Con_el_desnivel_a_cero_la_proteccion_esta_apagada()
    {
        var rig = Sitting(Options(delta: 0));
        StateChange? change = null;
        for (var i = 0; i < 10 && change is null; i++)
        {
            rig.Real(-87);
            change = rig.Step(TimeSpan.FromMilliseconds(500));
        }

        Assert.Equal(ProximityAction.ShowLockPrompt, change?.Action);
    }

    [Fact]
    public void Una_copia_como_disparo_usa_las_referencias_anteriores_a_su_lectura_real()
    {
        // El −87 real llega y en el mismo instante su copia: la referencia no puede incluir el propio −87
        var rig = Sitting();
        rig.Real(-87);
        rig.Time.Advance(TimeSpan.FromMilliseconds(300));
        rig.Copy(-87);
        Assert.Null(rig.Sm.Evaluate(rig.History));
        Assert.Equal(BatchKind.SuddenDrop, rig.Sm.Progress?.Kind);
    }

    [Fact]
    public void El_progreso_de_la_observacion_va_en_segundos()
    {
        var rig = Sitting();
        rig.Real(-87);
        rig.Step(TimeSpan.FromSeconds(12));

        var progress = rig.Sm.Progress;
        Assert.Equal(BatchKind.SuddenDrop, progress?.Kind);
        Assert.Equal(12, progress!.Count);
        Assert.Equal(30, progress.Target);
        Assert.Equal("突降确认 12/30 秒", rig.Sm.BatchProgressText);
    }

    [Fact]
    public void Tras_Win_L_no_se_vigilan_las_caidas()
    {
        // Elección del usuario: la confirmación de salida tras Win+L sigue con el lote normal
        var rig = Sitting();
        rig.Sm.OnSessionEvent(SessionEvent.Lock);
        StateChange? change = null;
        for (var i = 0; i < 10 && change is null; i++)
        {
            rig.Real(-87);
            change = rig.Step(TimeSpan.FromMilliseconds(500));
        }

        Assert.Equal(ProximityState.LockedArmed, change?.To);
    }

    // —— Subida brusca ——

    // Bloqueado y armado, con lecturas reales lejanas (-85) recientes
    private static Rig Away()
    {
        var time = new FakeTimeProvider(T0);
        var history = new RssiHistory(TimeSpan.FromSeconds(120), time);
        var sm = new ProximityStateMachine(time, Options(), new NotificationOptions(), true);
        var rig = new Rig(sm, time, history);
        time.Advance(TimeSpan.FromSeconds(20));
        for (var i = 0; i < 25 && sm.State != ProximityState.LockedArmed; i++)
        {
            rig.Real(-85);
            rig.Step(TimeSpan.FromMilliseconds(500));
        }

        Assert.Equal(ProximityState.LockedArmed, sm.State);
        for (var i = 0; i < 10; i++)
        {
            rig.Real(-85);
            rig.Step(TimeSpan.FromMilliseconds(500));
        }

        return rig;
    }

    [Fact]
    public void Una_subida_brusca_repetida_por_el_muestreo_no_desbloquea()
    {
        // Un pico suelto copiado cada 300 ms juntaba los 3 «lecturas» de desbloqueo en un segundo
        var rig = Away();
        rig.Real(-30);
        for (var i = 0; i < 30; i++)
        {
            rig.Time.Advance(TimeSpan.FromMilliseconds(300));
            rig.Copy(-30);
            Assert.Null(rig.Sm.Evaluate(rig.History));
        }

        Assert.Equal(ProximityState.LockedArmed, rig.Sm.State);
        Assert.Contains(rig.Sm.DrainNotes(), n => n.StartsWith("突升观察结束"));
    }

    [Fact]
    public void Si_tras_la_subida_la_senal_cae_no_desbloquea()
    {
        var rig = Away();
        rig.Real(-30);
        Assert.Null(rig.Step(TimeSpan.FromSeconds(1)));
        rig.Real(-80);
        Assert.Null(rig.Step(TimeSpan.FromSeconds(1)));

        Assert.Contains(rig.Sm.DrainNotes(), n => n.StartsWith("突升观察取消"));
        Assert.Equal(ProximityState.LockedArmed, rig.Sm.State);
    }

    [Fact]
    public void Una_subida_brusca_que_se_mantiene_desbloquea_a_los_cinco_segundos()
    {
        var rig = Away();
        var start = rig.Now;
        rig.Real(-35);
        StateChange? change = null;
        for (var i = 0; i < 10 && change is null; i++)
        {
            rig.Real(-34);
            change = rig.Step(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(ProximityAction.SendCredentials, change?.Action);
        Assert.Equal(TimeSpan.FromSeconds(5), rig.Now - start);
    }

    [Fact]
    public void Una_subida_gradual_desbloquea_como_siempre()
    {
        // Volver andando: 25 lecturas que suben poco a poco de -82 a -51 (pocas lecturas no bastan: con 8 la media
        // de las 25 seguiría cerca de -85 y la vuelta contaría como subida brusca, 5 s de espera)
        var rig = Away();
        for (var i = 0; i < 25; i++)
        {
            rig.Real((short)(-82 + (int)Math.Round(i * 31 / 24.0)));
            Assert.Null(rig.Step(TimeSpan.FromMilliseconds(500)));
        }

        // El disparo (-48) no está a 20 dB de la media de las últimas 25 reales: lote normal (disparo + 3)
        StateChange? change = null;
        var steps = 0;
        for (; steps < 10 && change is null; steps++)
        {
            rig.Real(-48);
            change = rig.Step(TimeSpan.FromMilliseconds(500));
        }

        Assert.Equal(ProximityAction.SendCredentials, change?.Action);
        Assert.Equal(4, steps);
    }

    // —— Histórico y configuración ——

    [Fact]
    public void Las_referencias_son_solo_lecturas_reales_recientes()
    {
        var time = new FakeTimeProvider(T0);
        var history = new RssiHistory(TimeSpan.FromSeconds(600), time);
        history.Add(-90, T0.AddSeconds(-200));            // fuera de los 120 s
        history.Add(-40, T0.AddSeconds(-10));
        history.Add(-40, T0.AddSeconds(-9), real: false); // copia: no cuenta
        history.Add(-42, T0.AddSeconds(-5));
        history.Add(-44, T0);                             // en el límite: excluida

        Assert.Equal(new short[] { -40, -42 }, history.RealBefore(T0, TimeSpan.FromSeconds(120), 25));
        Assert.Equal(new short[] { -42 }, history.RealBefore(T0, TimeSpan.FromSeconds(120), 1));
    }

    [Fact]
    public void El_muestreador_marca_las_copias()
    {
        var sampler = new SampleResampler(new FakeTimeProvider(T0));
        sampler.Offer(-60);

        Assert.Equal((short)-60, sampler.Take(true, TimeSpan.Zero, out var first));
        Assert.True(first);
        Assert.Equal((short)-60, sampler.Take(true, TimeSpan.Zero, out var second));
        Assert.False(second);
    }

    [Fact]
    public void Valores_por_defecto()
    {
        var p = new ProximityOptions();
        Assert.Equal(25, p.SuddenChangeReferenceCount);
        Assert.Equal(20, p.SuddenChangeDeltaDb);
        Assert.Equal(30, p.SuddenDropConfirmSeconds);
        Assert.Equal(5, p.SuddenRiseConfirmSeconds);
    }

    [Theory]
    [InlineData(5, 0, 5, 1, true)]
    [InlineData(50, 60, 300, 60, true)]
    [InlineData(25, 5, 30, 5, true)]
    [InlineData(4, 20, 30, 5, false)]
    [InlineData(51, 20, 30, 5, false)]
    [InlineData(25, 4, 30, 5, false)]
    [InlineData(25, 61, 30, 5, false)]
    [InlineData(25, 20, 4, 5, false)]
    [InlineData(25, 20, 301, 5, false)]
    [InlineData(25, 20, 30, 0.5, false)]
    [InlineData(25, 20, 30, 61, false)]
    public void Los_ajustes_tienen_limites(int count, double delta, double drop, double rise, bool valid)
    {
        var options = new BleUnlockOptions
        {
            Proximity = new ProximityOptions
            {
                SuddenChangeReferenceCount = count,
                SuddenChangeDeltaDb = delta,
                SuddenDropConfirmSeconds = drop,
                SuddenRiseConfirmSeconds = rise,
            },
        };
        Assert.Equal(valid, OptionsValidator.Validate(options).IsValid);
    }
}

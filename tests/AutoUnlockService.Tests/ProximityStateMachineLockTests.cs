using AutoUnlockService.Config;
using AutoUnlockService.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AutoUnlockService.Tests;

// 判定改版（2026-10-18，二次修订）：批次全数达标（每条读数都需达标，不取平均）。
// 触发读数不计入；锁定侧的静默（连续无读数超过 3 s 宽限）每秒注入一个极低值并
// 计入当前批次，静默本身即触发
public class ProximityStateMachineLockTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static (ProximityStateMachine Sm, FakeTimeProvider Time, RssiHistory History) Build(
        bool screenLocked = false)
    {
        var time = new FakeTimeProvider(T0);
        var history = new RssiHistory(TimeSpan.FromSeconds(120), time);
        var sm = new ProximityStateMachine(time, new ProximityOptions(), new NotificationOptions(), screenLocked);
        return (sm, time, history);
    }

    // Rellena con muestras del nivel indicado, una por segundo hacia atras
    private static void Fill(RssiHistory h, FakeTimeProvider time, short rssi, int count)
    {
        var now = time.GetUtcNow();
        for (int i = 0; i < count; i++)
            h.Add(rssi, now.AddSeconds(-i));
    }

    [Fact]
    public void Arranca_desbloqueado_si_la_pantalla_no_lo_esta()
        => Assert.Equal(ProximityState.Unlocked, Build().Sm.State);

    [Fact]
    public void Arranca_esperando_la_salida_si_la_pantalla_ya_estaba_bloqueada()
        => Assert.Equal(ProximityState.LockedWaitingDeparture, Build(screenLocked: true).Sm.State);

    [Fact]
    public void No_bloquea_durante_el_periodo_de_gracia_inicial()
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);                      // el dispositivo existe
        time.Advance(TimeSpan.FromSeconds(14));    // la gracia son 15 s

        Assert.Null(sm.Evaluate(history));
        Assert.Equal(ProximityState.Unlocked, sm.State);
    }

    [Fact]
    public void No_bloquea_si_el_dispositivo_objetivo_nunca_ha_aparecido()
    {
        var (sm, time, history) = Build();
        // Sin ninguna muestra: recien instalado, el reloj todavia no se ha puesto
        time.Advance(TimeSpan.FromSeconds(600));

        Assert.Null(sm.Evaluate(history));
        Assert.Equal(ProximityState.Unlocked, sm.State);
    }

    [Fact]
    public void El_silencio_llena_el_lote_y_pide_confirmacion()
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);
        time.Advance(TimeSpan.FromSeconds(30));    // pasada la gracia: 27 s de silencio

        var change = sm.Evaluate(history);

        Assert.NotNull(change);
        Assert.Equal(ProximityState.PendingLock, change.To);
        Assert.Equal(ProximityAction.ShowLockPrompt, change.Action);
        Assert.Contains("静默", change.Reason);    // 5 个极低值全部来自静默注入
    }

    [Fact]
    public void Pide_confirmacion_tras_el_disparador_y_cinco_lecturas_bajas()
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);
        time.Advance(TimeSpan.FromSeconds(20));
        Fill(history, time, -85, 6);               // 1 触发（不计入）+ 5 计入 → 全部 < -80

        var change = sm.Evaluate(history);

        Assert.Equal(ProximityState.PendingLock, change?.To);
        Assert.Contains("5 个读数全部 < -80 dBm", change!.Reason);
        Assert.DoesNotContain("静默", change.Reason);
    }

    [Fact]
    public void El_disparador_no_cuenta_para_el_lote()
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);
        time.Advance(TimeSpan.FromSeconds(20));
        Fill(history, time, -85, 5);               // 触发 + 4 → 还差一个

        Assert.Null(sm.Evaluate(history));
        Assert.Equal(ProximityState.Unlocked, sm.State);
    }

    [Fact]
    public void Un_lote_que_no_confirma_se_descarta_entero_y_hace_falta_otro_disparo()
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);
        time.Advance(TimeSpan.FromSeconds(20));

        history.Add(-85, time.GetUtcNow());        // disparador
        Assert.Null(sm.Evaluate(history));

        for (var i = 0; i < 5; i++)                // 5 个 -70：每条不达标 → 整批作废
        {
            time.Advance(TimeSpan.FromSeconds(1));
            history.Add(-70, time.GetUtcNow());
        }
        Assert.Null(sm.Evaluate(history));
        Assert.Equal(ProximityState.Unlocked, sm.State);

        // 整批作废；新触发后从零开始，凑满 5 个才确认
        for (var i = 0; i < 6; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            history.Add(-85, time.GetUtcNow());
            var change = sm.Evaluate(history);
            if (i < 5) Assert.Null(change);
            else Assert.Equal(ProximityState.PendingLock, change?.To);
        }
    }

    [Fact]
    public void Una_lectura_en_zona_muerta_dentro_del_lote_anula_toda_la_confirmacion()
    {
        // 回归：偶尔一条偏高的抖动读数（信号恢复一拍）不应把整批判成「离开」——
        // 旧的平均值实现会因 (−85×4 + −70)/5 = −82 < −80 而误弹窗；全数达标不会
        var (sm, time, history) = Build();
        history.Add(-50, T0);
        time.Advance(TimeSpan.FromSeconds(20));

        history.Add(-85, time.GetUtcNow());        // 触发
        for (var i = 1; i <= 4; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            history.Add(-85, time.GetUtcNow());    // 计入 4
        }
        time.Advance(TimeSpan.FromSeconds(1));
        history.Add(-70, time.GetUtcNow());        // 抖动：不达标 → 整批作废

        Assert.Null(sm.Evaluate(history));
        Assert.Equal(ProximityState.Unlocked, sm.State);
    }

    [Fact]
    public void El_silencio_completa_un_lote_a_medias()
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);
        time.Advance(TimeSpan.FromSeconds(20));

        history.Add(-85, time.GetUtcNow());        // disparador
        time.Advance(TimeSpan.FromSeconds(1));
        history.Add(-84, time.GetUtcNow());        // 计入 1
        time.Advance(TimeSpan.FromSeconds(1));
        history.Add(-83, time.GetUtcNow());        // 计入 2
        Assert.Null(sm.Evaluate(history));

        // El usuario se va: 3 个静默注入 completan los 5
        time.Advance(TimeSpan.FromSeconds(6));
        var change = sm.Evaluate(history);

        Assert.Equal(ProximityState.PendingLock, change?.To);
        Assert.Contains("静默", change!.Reason);
    }

    [Fact]
    public void La_gracia_de_silencio_retrasa_las_inyecciones()
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);
        time.Advance(TimeSpan.FromSeconds(20));

        history.Add(-85, time.GetUtcNow());        // disparador
        time.Advance(TimeSpan.FromSeconds(1));
        history.Add(-84, time.GetUtcNow());        // 计入 1
        time.Advance(TimeSpan.FromSeconds(1));
        history.Add(-83, time.GetUtcNow());        // 计入 2
        Assert.Null(sm.Evaluate(history));

        time.Advance(TimeSpan.FromSeconds(5));     // 5 s de silencio → solo 2 inyecciones
        Assert.Null(sm.Evaluate(history));

        time.Advance(TimeSpan.FromSeconds(1));     // 6 s → 3 inyecciones → lote completo
        Assert.Equal(ProximityState.PendingLock, sm.Evaluate(history)?.To);
    }

    [Fact]
    public void Cancela_el_aviso_si_la_senal_vuelve()
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);
        time.Advance(TimeSpan.FromSeconds(30));
        sm.Evaluate(history);
        Assert.Equal(ProximityState.PendingLock, sm.State);

        // 恢复：触发 -40 + 3 个 -40 → 全部 > -45
        time.Advance(TimeSpan.FromSeconds(5));
        Fill(history, time, -40, 5);

        var change = sm.Evaluate(history);

        Assert.Equal(ProximityState.Unlocked, change?.To);
        Assert.Equal(ProximityAction.CancelLockPrompt, change!.Action);
    }

    [Fact]
    public void Tres_lecturas_fuertes_no_cancelan_el_aviso_si_falta_una()
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);
        time.Advance(TimeSpan.FromSeconds(30));
        sm.Evaluate(history);
        Assert.Equal(ProximityState.PendingLock, sm.State);

        time.Advance(TimeSpan.FromSeconds(4));
        Fill(history, time, -30, 3);               // 触发 + 2 计入 → 还差一个

        Assert.Null(sm.Evaluate(history));
        Assert.Equal(ProximityState.PendingLock, sm.State);
    }

    [Fact]
    public void Tras_el_aviso_el_silencio_confirma_el_bloqueo()
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);
        time.Advance(TimeSpan.FromSeconds(30));
        sm.Evaluate(history);

        time.Advance(TimeSpan.FromSeconds(2));     // dentro de la gracia de silencio
        Assert.Null(sm.Evaluate(history));

        time.Advance(TimeSpan.FromSeconds(11));    // 13 s → 10 inyecciones completan el lote
        var change = sm.Evaluate(history);

        Assert.Equal(ProximityState.LockedWaitingDeparture, change?.To);
        Assert.Equal(ProximityAction.LockScreen, change!.Action);
    }

    [Fact]
    public void Las_lecturas_en_zona_muerta_mantienen_el_aviso_y_no_bloquean()
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);
        time.Advance(TimeSpan.FromSeconds(30));
        sm.Evaluate(history);
        Assert.Equal(ProximityState.PendingLock, sm.State);

        // -80 ~ -65：ni cerca ni lejos. El aviso se queda; nada lo bloquea por tiempo
        for (var i = 0; i < 6; i++)
        {
            time.Advance(TimeSpan.FromSeconds(5));
            Fill(history, time, -70, 5);
            Assert.Null(sm.Evaluate(history));
            Assert.Equal(ProximityState.PendingLock, sm.State);
        }
    }

    [Fact]
    public void Respuesta_de_rechazo_abre_un_silencio_de_sesenta_segundos()
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);
        time.Advance(TimeSpan.FromSeconds(30));
        sm.Evaluate(history);

        var change = sm.OnPromptResponse(PromptResponse.Reject);
        Assert.Equal(ProximityState.Snoozed, change?.To);

        time.Advance(TimeSpan.FromSeconds(59));
        Assert.Null(sm.Evaluate(history));         // sigue en silencio

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(ProximityState.Unlocked, sm.Evaluate(history)?.To);
    }

    [Fact]
    public void Respuesta_de_aplazamiento_silencia_cinco_minutos()
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);
        time.Advance(TimeSpan.FromSeconds(30));
        sm.Evaluate(history);

        sm.OnPromptResponse(PromptResponse.Snooze);

        time.Advance(TimeSpan.FromSeconds(299));
        Assert.Null(sm.Evaluate(history));

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(ProximityState.Unlocked, sm.Evaluate(history)?.To);
    }

    [Fact]
    public void Cerrar_la_notificacion_bloquea_la_pantalla()
    {
        var (sm, _, _) = BuildPending();

        var change = sm.OnPromptResponse(PromptResponse.Dismissed);

        Assert.Equal(ProximityState.LockedWaitingDeparture, change?.To);
        Assert.Equal(ProximityAction.LockScreen, change!.Action);
    }

    [Fact]
    public void El_timeout_de_la_notificacion_ya_no_bloquea()
    {
        var (sm, _, _) = BuildPending();

        // La espera no tiene plazo automatico: el timeout solo indica que el dialogo
        // degradado se cerro solo. El bloqueo lo deciden las lecturas o el usuario
        Assert.Null(sm.OnPromptResponse(PromptResponse.Timeout));
        Assert.Equal(ProximityState.PendingLock, sm.State);
    }

    [Fact]
    public void Una_respuesta_fuera_de_PendingLock_se_ignora()
    {
        var (sm, _, _) = Build();
        Assert.Null(sm.OnPromptResponse(PromptResponse.Reject));
        Assert.Equal(ProximityState.Unlocked, sm.State);
    }

    [Fact]
    public void Un_bloqueo_externo_cancela_el_aviso_pendiente()
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);
        time.Advance(TimeSpan.FromSeconds(30));
        sm.Evaluate(history);

        var change = sm.OnSessionEvent(SessionEvent.Lock);

        Assert.Equal(ProximityState.LockedWaitingDeparture, change?.To);
        Assert.Equal(ProximityAction.CancelLockPrompt, change!.Action);
    }

    [Fact]
    public void Un_bloqueo_externo_desde_Unlocked_exige_salir_antes_de_desbloquear()
    {
        var (sm, _, _) = Build();

        var change = sm.OnSessionEvent(SessionEvent.Lock);

        Assert.Equal(ProximityState.LockedWaitingDeparture, change?.To);
    }

    [Fact]
    public void Un_desbloqueo_manual_devuelve_el_estado_a_Unlocked()
    {
        var (sm, _, _) = Build(screenLocked: true);

        var change = sm.OnSessionEvent(SessionEvent.Unlock);

        Assert.Equal(ProximityState.Unlocked, change?.To);
    }

    [Fact]
    public void OnResume_reabre_el_periodo_de_gracia_tras_un_salto_de_reloj()
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);
        time.Advance(TimeSpan.FromSeconds(30));    // la gracia inicial ya paso

        // El portatil estuvo suspendido tres horas; el orquestador vacia el historico
        history.Clear();
        time.Advance(TimeSpan.FromHours(3));
        sm.OnResume();

        Assert.Null(sm.Evaluate(history));         // no bloquea nada mas abrir la tapa
        Assert.Equal(ProximityState.Unlocked, sm.State);

        time.Advance(TimeSpan.FromSeconds(16));    // 16 s de silencio tras la gracia reabierta
        Assert.Equal(ProximityState.PendingLock, sm.Evaluate(history)?.To);
    }

    // Deja la maquina en PendingLock con el aviso en pantalla
    private static (ProximityStateMachine Sm, FakeTimeProvider Time, RssiHistory History) BuildPending()
    {
        var b = Build();
        b.History.Add(-50, T0);
        b.Time.Advance(TimeSpan.FromSeconds(30));
        b.Sm.Evaluate(b.History);
        Assert.Equal(ProximityState.PendingLock, b.Sm.State);
        return b;
    }

    [Fact]
    public void Una_pausa_corta_no_cancela_el_aviso_pendiente()
    {
        var (sm, time, history) = BuildPending();
        time.Advance(TimeSpan.FromSeconds(2));     // dentro de la gracia de silencio

        Assert.Null(sm.Evaluate(history));
        Assert.Equal(ProximityState.PendingLock, sm.State);
    }

    [Fact]
    public void OnResume_en_PendingLock_cancela_el_aviso_y_reabre_la_gracia()
    {
        var (sm, time, history) = BuildPending();

        time.Advance(TimeSpan.FromHours(3));       // el aviso llevaba horas en pantalla
        history.Clear();
        var change = sm.OnResume();

        Assert.Equal(ProximityState.Unlocked, change?.To);
        Assert.Equal(ProximityAction.CancelLockPrompt, change!.Action);
        Assert.Equal(ProximityState.Unlocked, sm.State);

        Assert.Null(sm.Evaluate(history));         // la gracia vuelve a regir: no bloquea
        Assert.Equal(ProximityState.Unlocked, sm.State);
    }

    [Fact]
    public void OnResume_fuera_de_PendingLock_no_cambia_el_estado()
    {
        var (sm, _, _) = Build();
        Assert.Null(sm.OnResume());
    }

    [Fact]
    public void Un_desbloqueo_manual_desde_PendingLock_retira_el_aviso()
    {
        var (sm, _, _) = BuildPending();

        var change = sm.OnSessionEvent(SessionEvent.Unlock);

        Assert.Equal(ProximityState.Unlocked, change?.To);
        Assert.Equal(ProximityAction.CancelLockPrompt, change!.Action);
    }
}

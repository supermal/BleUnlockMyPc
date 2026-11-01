using BleUnlockService.Config;
using BleUnlockService.Core;
using BleUnlockService.Credentials;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BleUnlockService.Tests;

// 判定改版（2026-10-18，二次修订）：解锁 = 第一条 > 阈值触发（不计入）+ 再收 3 条
// 且全部 > 阈值（不取平均）。批次内间隙超过静默宽限（3 s）时旧值作废，重新触发
public class ProximityStateMachineUnlockTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static (ProximityStateMachine Sm, FakeTimeProvider Time, RssiHistory History) Build()
    {
        var time = new FakeTimeProvider(T0);
        var history = new RssiHistory(TimeSpan.FromSeconds(120), time);
        // Arrancamos con la pantalla ya bloqueada: es el escenario de esta bateria
        var sm = new ProximityStateMachine(time, new ProximityOptions(), new NotificationOptions(), true);
        return (sm, time, history);
    }

    private static void Fill(RssiHistory h, FakeTimeProvider time, short rssi, int count)
    {
        var now = time.GetUtcNow();
        for (int i = 0; i < count; i++)
            h.Add(rssi, now.AddSeconds(-i));
    }

    // Lleva la maquina hasta LockedArmed: el usuario se fue de verdad
    // (la senal primero se debilita por debajo del umbral; despues desaparece)
    private static void Arm(ProximityStateMachine sm, FakeTimeProvider time, RssiHistory history)
    {
        time.Advance(TimeSpan.FromSeconds(1));
        history.Add(-85, time.GetUtcNow());
        time.Advance(TimeSpan.FromSeconds(30));
        sm.Evaluate(history);
        Assert.Equal(ProximityState.LockedArmed, sm.State);
    }

    // El usuario vuelve: disparador -40 + 3 confirmaciones -40 (todo > -45)
    private static void Approach(RssiHistory h, FakeTimeProvider time)
    {
        time.Advance(TimeSpan.FromSeconds(5));
        Fill(h, time, -40, 5);
    }

    [Fact]
    public void Pegado_al_ordenador_tras_bloquear_no_desbloquea_nunca()
    {
        var (sm, time, history) = Build();

        // El usuario pulso Win+L y sigue sentado: senal fortisima, pero nunca se fue
        for (int i = 0; i < 20; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            Fill(history, time, -30, 5);
            var change = sm.Evaluate(history);
            Assert.NotEqual(ProximityAction.SendCredentials, change?.Action ?? ProximityAction.None);
        }

        Assert.Equal(ProximityState.LockedWaitingDeparture, sm.State);
    }

    [Fact]
    public void Tras_detectar_la_salida_queda_armado()
    {
        var (sm, time, history) = Build();
        time.Advance(TimeSpan.FromSeconds(1));
        history.Add(-85, time.GetUtcNow());       // 信号先变弱再消失：真离开的形状
        time.Advance(TimeSpan.FromSeconds(30));   // silencio: la salida se confirma sola

        var change = sm.Evaluate(history);

        Assert.Equal(ProximityState.LockedArmed, change?.To);
        Assert.Equal(ProximityAction.None, change!.Action);
    }

    [Fact]
    public void Armado_y_cerca_envia_las_credenciales_una_sola_vez()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);

        Approach(history, time);

        var change = sm.Evaluate(history);
        Assert.Equal(ProximityState.UnlockPending, change?.To);
        Assert.Equal(ProximityAction.SendCredentials, change!.Action);

        // Mientras espera resultado no vuelve a enviar, aunque siga cerca
        time.Advance(TimeSpan.FromSeconds(1));
        Fill(history, time, -40, 5);
        Assert.Null(sm.Evaluate(history));
    }

    [Fact]
    public void El_resultado_correcto_devuelve_a_Unlocked()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        Approach(history, time);
        sm.Evaluate(history);

        var change = sm.OnAuthResult(new AuthResult(true, 0));

        Assert.Equal(ProximityState.Unlocked, change?.To);
    }

    [Fact]
    public void Un_desbloqueo_manual_durante_la_espera_tambien_cierra_el_ciclo()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        Approach(history, time);
        sm.Evaluate(history);

        var change = sm.OnSessionEvent(SessionEvent.Unlock);

        Assert.Equal(ProximityState.Unlocked, change?.To);
    }

    [Fact]
    public void Clave_erronea_deja_el_servicio_parado_y_avisa()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        Approach(history, time);
        sm.Evaluate(history);

        var change = sm.OnAuthResult(new AuthResult(false, NtStatusPolicy.StatusLogonFailure));

        Assert.Equal(ProximityState.Suspended, change?.To);
        Assert.Equal(ProximityAction.NotifySuspended, change!.Action);
    }

    [Fact]
    public void Un_fallo_que_no_es_de_credencial_solo_reintenta()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        Approach(history, time);
        sm.Evaluate(history);

        var change = sm.OnAuthResult(new AuthResult(false, 0xC0000022));

        Assert.Equal(ProximityState.LockedArmed, change?.To);
    }

    [Fact]
    public void Si_nunca_llega_el_resultado_vuelve_a_quedar_armado()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        Approach(history, time);
        sm.Evaluate(history);

        // ReportResult puede no llegar nunca si LogonUI se destruye
        time.Advance(TimeSpan.FromSeconds(21));   // UnlockPendingTimeoutSeconds = 20

        var change = sm.Evaluate(history);

        Assert.Equal(ProximityState.LockedArmed, change?.To);
        Assert.NotEqual(ProximityState.Suspended, sm.State);
    }

    [Fact]
    public void Suspended_no_se_sale_con_un_desbloqueo_manual()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        Approach(history, time);
        sm.Evaluate(history);
        sm.OnAuthResult(new AuthResult(false, NtStatusPolicy.StatusLogonFailure));

        // El usuario entra escribiendo la clave a mano; eso no arregla el fichero
        Assert.Null(sm.OnSessionEvent(SessionEvent.Unlock));
        Assert.Equal(ProximityState.Suspended, sm.State);

        Assert.Null(sm.OnSessionEvent(SessionEvent.Lock));
        Assert.Equal(ProximityState.Suspended, sm.State);
    }

    [Fact]
    public void Suspended_ignora_las_muestras()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        Approach(history, time);
        sm.Evaluate(history);
        sm.OnAuthResult(new AuthResult(false, NtStatusPolicy.StatusLogonFailure));

        time.Advance(TimeSpan.FromSeconds(5));
        Fill(history, time, -40, 5);

        Assert.Null(sm.Evaluate(history));
        Assert.Equal(ProximityState.Suspended, sm.State);
    }

    [Fact]
    public void Corregir_la_clave_reanuda_el_servicio_en_el_estado_correcto()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        Approach(history, time);
        sm.Evaluate(history);
        sm.OnAuthResult(new AuthResult(false, NtStatusPolicy.StatusLogonFailure));

        // La pantalla sigue bloqueada, asi que hay que volver a exigir la salida
        var change = sm.OnCredentialsChanged(preValidationPassed: true);

        Assert.Equal(ProximityState.LockedWaitingDeparture, change?.To);
    }

    [Fact]
    public void Corregir_la_clave_con_la_pantalla_abierta_reanuda_en_Unlocked()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        Approach(history, time);
        sm.Evaluate(history);
        sm.OnAuthResult(new AuthResult(false, NtStatusPolicy.StatusLogonFailure));

        sm.OnSessionEvent(SessionEvent.Unlock);   // el usuario entro a mano
        var change = sm.OnCredentialsChanged(preValidationPassed: true);

        Assert.Equal(ProximityState.Unlocked, change?.To);
    }

    [Fact]
    public void Una_clave_que_sigue_sin_validar_no_reanuda()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        Approach(history, time);
        sm.Evaluate(history);
        sm.OnAuthResult(new AuthResult(false, NtStatusPolicy.StatusLogonFailure));

        Assert.Null(sm.OnCredentialsChanged(preValidationPassed: false));
        Assert.Equal(ProximityState.Suspended, sm.State);
    }

    [Fact]
    public void Una_credencial_que_no_valida_para_el_servicio_aunque_no_hubiera_fallado_antes()
    {
        var (sm, _, _) = Build();

        var change = sm.OnCredentialsChanged(preValidationPassed: false);

        Assert.Equal(ProximityState.Suspended, change?.To);
        Assert.Equal(ProximityAction.NotifySuspended, change!.Action);
    }

    [Fact]
    public void Armado_pero_con_senal_debil_no_envia_credenciales()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);

        time.Advance(TimeSpan.FromSeconds(5));
        Fill(history, time, -60, 5);   // entre -80 y -45: zona muerta

        Assert.Null(sm.Evaluate(history));
        Assert.Equal(ProximityState.LockedArmed, sm.State);
    }

    [Fact]
    public void Pegado_al_ordenador_tras_bloquear_no_desbloquea_nunca_ni_con_senal_maxima_y_mucho_tiempo()
    {
        var (sm, time, history) = Build();

        // Mas de dos minutos con senal altisima: la guardia de salida no caduca por tiempo
        for (int i = 0; i < 150; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            Fill(history, time, -20, 5);
            Assert.Null(sm.Evaluate(history));
            Assert.Equal(ProximityState.LockedWaitingDeparture, sm.State);
        }
    }

    [Fact]
    public void Tras_irse_y_volver_el_ciclo_completo_envia_credenciales()
    {
        var (sm, time, history) = Build();
        time.Advance(TimeSpan.FromSeconds(1));
        history.Add(-85, time.GetUtcNow());                // 信号已经在变弱
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(sm.Evaluate(history));                 // 还差一个：宽限内不注入
        time.Advance(TimeSpan.FromSeconds(30));            // se va (silencio)
        Assert.Equal(ProximityState.LockedArmed, sm.Evaluate(history)?.To);
        Approach(history, time);                           // vuelve
        Assert.Equal(ProximityAction.SendCredentials, sm.Evaluate(history)?.Action);
    }

    [Fact]
    public void Un_hueco_en_el_lote_de_aproximacion_invalida_lo_acumulado()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);

        // disparador + 2 confirmaciones, luego 10 s sin lecturas
        time.Advance(TimeSpan.FromSeconds(5));
        history.Add(-40, time.GetUtcNow());
        time.Advance(TimeSpan.FromSeconds(1));
        history.Add(-40, time.GetUtcNow());
        time.Advance(TimeSpan.FromSeconds(1));
        history.Add(-40, time.GetUtcNow());        // 2/3
        Assert.Null(sm.Evaluate(history));

        time.Advance(TimeSpan.FromSeconds(10));
        history.Add(-40, time.GetUtcNow());        // hueco > 3 s: el lote viejo se tira
        Assert.Null(sm.Evaluate(history));         // esta lectura solo dispara

        time.Advance(TimeSpan.FromSeconds(1));
        history.Add(-40, time.GetUtcNow());        // 1/3
        Assert.Null(sm.Evaluate(history));

        time.Advance(TimeSpan.FromSeconds(1));
        history.Add(-40, time.GetUtcNow());        // 2/3
        Assert.Null(sm.Evaluate(history));

        time.Advance(TimeSpan.FromSeconds(1));
        history.Add(-40, time.GetUtcNow());        // 3/3
        Assert.Equal(ProximityAction.SendCredentials, sm.Evaluate(history)?.Action);
    }

    [Fact]
    public void Un_lote_de_aproximacion_que_no_confirma_se_descarta()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);

        time.Advance(TimeSpan.FromSeconds(5));
        history.Add(-40, time.GetUtcNow());        // disparador
        for (var i = 0; i < 3; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            history.Add(-50, time.GetUtcNow());    // -50 ≤ umbral: 第一条即作废整批
        }
        Assert.Null(sm.Evaluate(history));

        // 重新触发：-40 作触发 + 3 个 -40 才确认
        for (var i = 0; i < 4; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            history.Add(-40, time.GetUtcNow());
            var change = sm.Evaluate(history);
            if (i < 3) Assert.Null(change);
            else Assert.Equal(ProximityAction.SendCredentials, change?.Action);
        }
    }

    [Fact]
    public void Una_lectura_baja_dentro_del_lote_de_aproximacion_anula_la_confirmacion()
    {
        // 回归：确认批次里一条 -50 不应把整批「平均」到可以解锁——
        // (−40 −40 −50)/3 = −43.3 > −45：旧平均值实现会发送凭据；全数达标不会
        var (sm, time, history) = Build();
        Arm(sm, time, history);

        time.Advance(TimeSpan.FromSeconds(5));
        history.Add(-40, time.GetUtcNow());        // 触发
        time.Advance(TimeSpan.FromSeconds(1));
        history.Add(-40, time.GetUtcNow());        // 计入 1
        time.Advance(TimeSpan.FromSeconds(1));
        history.Add(-40, time.GetUtcNow());        // 计入 2
        time.Advance(TimeSpan.FromSeconds(1));
        history.Add(-50, time.GetUtcNow());        // 掉档：整批作废

        Assert.Null(sm.Evaluate(history));
        Assert.Equal(ProximityState.LockedArmed, sm.State);
    }

    [Theory]
    [InlineData(0xC000006Du)]
    [InlineData(0xC0000071u)]
    [InlineData(0xC0000224u)]
    [InlineData(0xC0000234u)]
    public void Los_cuatro_fallos_de_credencial_paran_el_servicio(uint status)
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        Approach(history, time);
        sm.Evaluate(history);

        Assert.Equal(ProximityState.Suspended, sm.OnAuthResult(new AuthResult(false, status))?.To);
    }

    [Fact]
    public void Un_resultado_fuera_de_UnlockPending_se_ignora()
    {
        var (sm, _, _) = Build();

        Assert.Null(sm.OnAuthResult(new AuthResult(false, NtStatusPolicy.StatusLogonFailure)));
        Assert.Equal(ProximityState.LockedWaitingDeparture, sm.State);
    }

    [Fact]
    public void Antes_del_plazo_UnlockPending_sigue_esperando()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        Approach(history, time);
        sm.Evaluate(history);

        time.Advance(TimeSpan.FromSeconds(19));
        Assert.Null(sm.Evaluate(history));
        Assert.Equal(ProximityState.UnlockPending, sm.State);
    }

    [Fact]
    public void Tras_el_timeout_si_sigue_cerca_reintenta_el_envio()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        Approach(history, time);
        sm.Evaluate(history);

        time.Advance(TimeSpan.FromSeconds(21));
        Assert.Equal(ProximityState.LockedArmed, sm.Evaluate(history)?.To);

        Approach(history, time);
        Assert.Equal(ProximityAction.SendCredentials, sm.Evaluate(history)?.Action);
    }

    [Fact]
    public void Suspended_recuerda_el_bloqueo_ocurrido_mientras_estaba_parado()
    {
        var (sm, _, _) = Build(); // pantalla bloqueada al arrancar
        sm.OnCredentialsChanged(preValidationPassed: false);
        sm.OnSessionEvent(SessionEvent.Unlock);
        sm.OnSessionEvent(SessionEvent.Lock);

        Assert.Equal(ProximityState.LockedWaitingDeparture,
            sm.OnCredentialsChanged(preValidationPassed: true)?.To);
    }

    [Fact]
    public void Una_credencial_valida_fuera_de_Suspended_no_cambia_nada()
    {
        var (sm, _, _) = Build();

        Assert.Null(sm.OnCredentialsChanged(preValidationPassed: true));
        Assert.Equal(ProximityState.LockedWaitingDeparture, sm.State);
    }

    [Fact]
    public void Tras_reanudar_hay_que_volver_a_exigir_la_salida_antes_de_enviar()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        Approach(history, time);
        sm.Evaluate(history);
        sm.OnAuthResult(new AuthResult(false, NtStatusPolicy.StatusLogonFailure));
        sm.OnCredentialsChanged(preValidationPassed: true);

        time.Advance(TimeSpan.FromSeconds(5));
        Fill(history, time, -30, 5);
        Assert.Null(sm.Evaluate(history));
        Assert.Equal(ProximityState.LockedWaitingDeparture, sm.State);
    }

    [Fact]
    public void Arrancando_bloqueado_con_historico_vacio_dentro_de_la_gracia_no_arma()
    {
        var (sm, time, history) = Build();
        time.Advance(TimeSpan.FromSeconds(14));   // la gracia son 15 s

        Assert.Null(sm.Evaluate(history));
        Assert.Equal(ProximityState.LockedWaitingDeparture, sm.State);
    }

    [Fact]
    public void Arrancando_bloqueado_sin_senal_pasada_la_gracia_si_arma()
    {
        var (sm, time, history) = Build();
        time.Advance(TimeSpan.FromSeconds(16));   // la gracia son 15 s: ya paso, pero aun no hay 60 s sin senal
        Assert.Null(sm.Evaluate(history));

        time.Advance(TimeSpan.FromSeconds(45));   // 61 s sin ninguna lectura：兜底 → LockedArmed
        Assert.Equal(ProximityState.LockedArmed, sm.Evaluate(history)?.To);
    }

    [Fact]
    public void Win_L_sentado_con_suspension_y_despertar_no_desbloquea()
    {
        var (sm, time, history) = Build();
        time.Advance(TimeSpan.FromSeconds(60));   // la gracia inicial ya paso
        Fill(history, time, -30, 5);
        Assert.Null(sm.Evaluate(history));

        // La pantalla duerme y el usuario despierta el equipo: el orquestador vacia el historico
        history.Clear();
        sm.OnResume();

        for (int i = 0; i < 14; i++)
        {
            Assert.Null(sm.Evaluate(history));    // sin datos aun: no es "se fue"
            time.Advance(TimeSpan.FromSeconds(1));
            Fill(history, time, -30, 5);          // llegan las primeras emisiones
            var change = sm.Evaluate(history);
            Assert.NotEqual(ProximityAction.SendCredentials, change?.Action ?? ProximityAction.None);
            Assert.Equal(ProximityState.LockedWaitingDeparture, sm.State);
        }
    }

    [Fact]
    public void Suspender_por_credencial_invalida_desde_PendingLock_retira_el_aviso()
    {
        var time = new FakeTimeProvider(T0);
        var history = new RssiHistory(TimeSpan.FromSeconds(120), time);
        var sm = new ProximityStateMachine(time, new ProximityOptions(), new NotificationOptions(), false);
        time.Advance(TimeSpan.FromSeconds(1));
        history.Add(-85, time.GetUtcNow());        // 信号先变弱，静默才有离开证据
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(ProximityState.PendingLock, sm.Evaluate(history)?.To);

        var change = sm.OnCredentialsChanged(preValidationPassed: false);

        Assert.Equal(ProximityState.Suspended, change?.To);
        Assert.Equal(ProximityAction.CancelLockPromptAndNotifySuspended, change!.Action);
    }
}

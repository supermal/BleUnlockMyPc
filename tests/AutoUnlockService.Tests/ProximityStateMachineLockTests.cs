using AutoUnlockService.Config;
using AutoUnlockService.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AutoUnlockService.Tests;

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

    // Rellena la ventana con muestras del nivel indicado, una por segundo hacia atras
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
    public void Pide_confirmacion_cuando_desaparece_la_senal()
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);
        time.Advance(TimeSpan.FromSeconds(30));    // pasada la gracia y la ventana de 10 s

        var change = sm.Evaluate(history);

        Assert.NotNull(change);
        Assert.Equal(ProximityState.PendingLock, change.To);
        Assert.Equal(ProximityAction.ShowLockPrompt, change.Action);
        Assert.Contains("无信号", change.Reason);
    }

    [Fact]
    public void Pide_confirmacion_cuando_la_media_baja_del_umbral()
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);
        time.Advance(TimeSpan.FromSeconds(20));
        Fill(history, time, -85, 5);               // media -85 < -80

        var change = sm.Evaluate(history);

        Assert.Equal(ProximityState.PendingLock, change?.To);
        Assert.Contains("-85", change!.Reason);
    }

    [Fact]
    public void No_bloquea_con_menos_muestras_que_el_minimo()
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);
        time.Advance(TimeSpan.FromSeconds(20));
        Fill(history, time, -85, 2);               // MinSamples = 3

        Assert.Null(sm.Evaluate(history));
    }

    [Fact]
    public void Cancela_el_aviso_si_la_senal_vuelve()
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);
        time.Advance(TimeSpan.FromSeconds(30));
        sm.Evaluate(history);
        Assert.Equal(ProximityState.PendingLock, sm.State);

        time.Advance(TimeSpan.FromSeconds(2));
        Fill(history, time, -40, 5);               // media -40 > -45

        var change = sm.Evaluate(history);

        Assert.Equal(ProximityState.Unlocked, change?.To);
        Assert.Equal(ProximityAction.CancelLockPrompt, change!.Action);
    }

    [Fact]
    public void Bloquea_cuando_vence_el_plazo_del_aviso()
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);
        time.Advance(TimeSpan.FromSeconds(30));
        sm.Evaluate(history);

        time.Advance(TimeSpan.FromSeconds(15));    // PromptTimeoutSeconds = 15
        var change = sm.Evaluate(history);

        Assert.Equal(ProximityState.LockedWaitingDeparture, change?.To);
        Assert.Equal(ProximityAction.LockScreen, change!.Action);
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

    [Theory]
    [InlineData(PromptResponse.Timeout)]
    [InlineData(PromptResponse.Dismissed)]
    public void Timeout_y_descarte_bloquean_la_pantalla(PromptResponse response)
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);
        time.Advance(TimeSpan.FromSeconds(30));
        sm.Evaluate(history);

        var change = sm.OnPromptResponse(response);

        Assert.Equal(ProximityState.LockedWaitingDeparture, change?.To);
        Assert.Equal(ProximityAction.LockScreen, change!.Action);
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

        time.Advance(TimeSpan.FromSeconds(16));
        Assert.Equal(ProximityState.PendingLock, sm.Evaluate(history)?.To);
    }
}

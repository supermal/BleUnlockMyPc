using BleUnlockService.Config;
using BleUnlockService.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BleUnlockService.Tests;

// Receptor Bluetooth colgado (2026-11-01): Windows dice que escanea pero no llega nada de nadie.
// Mientras dure, el silencio no es prueba de que el usuario se haya ido
public class ProximityStateMachineScanStallTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 20, 0, 0, TimeSpan.Zero);

    private static (ProximityStateMachine Sm, FakeTimeProvider Time, RssiHistory History) Build(bool screenLocked = false)
    {
        var time = new FakeTimeProvider(T0);
        var history = new RssiHistory(TimeSpan.FromSeconds(120), time);
        var sm = new ProximityStateMachine(time, new ProximityOptions(), new NotificationOptions(), screenLocked);
        return (sm, time, history);
    }

    // Avanza segundo a segundo evaluando, como el bucle real; devuelve el primer cambio
    private static StateChange? RunFor(ProximityStateMachine sm, FakeTimeProvider time, RssiHistory history, int seconds)
    {
        for (var i = 0; i < seconds; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            if (sm.Evaluate(history) is { } change) return change;
        }

        return null;
    }

    [Fact]
    public void Con_el_receptor_colgado_el_silencio_no_lleva_al_aviso()
    {
        var (sm, time, history) = Build();
        time.Advance(TimeSpan.FromSeconds(1));
        history.Add(-85, time.GetUtcNow());     // la señal ya se había debilitado
        Assert.Null(sm.OnScanHealthChanged(stalled: true));

        Assert.Null(RunFor(sm, time, history, 600));
        Assert.Equal(ProximityState.Unlocked, sm.State);
    }

    [Fact]
    public void Con_el_receptor_colgado_las_lecturas_reales_siguen_contando()
    {
        var (sm, time, history) = Build();
        sm.OnScanHealthChanged(stalled: true);
        time.Advance(TimeSpan.FromSeconds(20));
        var now = time.GetUtcNow();
        for (var i = 5; i >= 0; i--) history.Add(-90, now.AddSeconds(-i));

        var change = sm.Evaluate(history);

        Assert.NotNull(change);
        Assert.Equal(ProximityState.PendingLock, change.To);
    }

    [Fact]
    public void Detectar_el_cuelgue_con_el_aviso_abierto_lo_retira()
    {
        var (sm, time, history) = Build();
        time.Advance(TimeSpan.FromSeconds(1));
        history.Add(-85, time.GetUtcNow());
        Assert.Equal(ProximityState.PendingLock, RunFor(sm, time, history, 30)?.To);

        var change = sm.OnScanHealthChanged(stalled: true);

        Assert.NotNull(change);
        Assert.Equal(ProximityState.Unlocked, change.To);
        Assert.Equal(ProximityAction.CancelLockPrompt, change.Action);
        // Y no vuelve a avisar mientras siga colgado
        Assert.Null(RunFor(sm, time, history, 300));
    }

    [Fact]
    public void Detectar_el_cuelgue_con_el_desbloqueo_armado_exige_volver_a_salir()
    {
        var (sm, time, history) = Build(screenLocked: true);
        time.Advance(TimeSpan.FromSeconds(1));
        history.Add(-85, time.GetUtcNow());
        Assert.Equal(ProximityState.LockedArmed, RunFor(sm, time, history, 30)?.To);

        var change = sm.OnScanHealthChanged(stalled: true);

        Assert.NotNull(change);
        Assert.Equal(ProximityState.LockedWaitingDeparture, change.To);
        Assert.Equal(ProximityAction.None, change.Action);
        // El silencio del cuelgue no vuelve a armarlo
        Assert.Null(RunFor(sm, time, history, 300));
        Assert.Equal(ProximityState.LockedWaitingDeparture, sm.State);
    }

    [Fact]
    public void Al_recuperarse_el_silencio_se_cuenta_desde_cero()
    {
        var (sm, time, history) = Build();
        time.Advance(TimeSpan.FromSeconds(1));
        history.Add(-85, time.GetUtcNow());     // señal débil justo antes del cuelgue
        sm.OnScanHealthChanged(stalled: true);
        RunFor(sm, time, history, 120);

        Assert.Null(sm.OnScanHealthChanged(stalled: false));

        // La lectura débil de antes del cuelgue ya no sirve de umbral: solo el respaldo de 60 s
        Assert.Null(RunFor(sm, time, history, 55));
        var change = RunFor(sm, time, history, 10);
        Assert.NotNull(change);
        Assert.Equal(ProximityState.PendingLock, change.To);
    }

    [Fact]
    public void Repetir_el_mismo_estado_del_receptor_no_hace_nada()
    {
        var (sm, _, _) = Build();
        Assert.Null(sm.OnScanHealthChanged(stalled: false));
        Assert.Null(sm.OnScanHealthChanged(stalled: true));
        Assert.Null(sm.OnScanHealthChanged(stalled: true));
    }
}

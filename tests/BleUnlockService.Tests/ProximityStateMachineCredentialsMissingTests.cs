using BleUnlockService.Config;
using BleUnlockService.Core;
using BleUnlockService.Credentials;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BleUnlockService.Tests;

// Equipo con dispositivo pero sin usuario o clave (2026-11-02): se bloquea al irse, nunca se desbloquea
public class ProximityStateMachineCredentialsMissingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static (ProximityStateMachine Sm, FakeTimeProvider Time, RssiHistory History) Build(bool screenLocked)
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

    // El usuario se va: la señal se debilita por debajo del umbral y después desaparece
    private static void Leave(FakeTimeProvider time, RssiHistory history)
    {
        // Dos lecturas débiles seguidas: con una sola, el silencio posterior ya no cuenta (2026-11-04)
        time.Advance(TimeSpan.FromSeconds(1));
        history.Add(-85, time.GetUtcNow().AddMilliseconds(-500));
        history.Add(-85, time.GetUtcNow());
    }

    [Fact]
    public void Sin_credenciales_el_aviso_y_el_bloqueo_siguen_funcionando()
    {
        var (sm, time, history) = Build(screenLocked: false);
        Assert.Null(sm.OnCredentialsMissing());

        Leave(time, history);
        var prompt = RunFor(sm, time, history, 60);
        Assert.Equal(ProximityState.PendingLock, prompt?.To);
        Assert.Equal(ProximityAction.ShowLockPrompt, prompt!.Action);

        var locked = RunFor(sm, time, history, 60);
        Assert.Equal(ProximityState.LockedWaitingDeparture, locked?.To);
        Assert.Equal(ProximityAction.LockScreen, locked!.Action);
    }

    [Fact]
    public void Sin_credenciales_el_desbloqueo_nunca_se_arma()
    {
        var (sm, time, history) = Build(screenLocked: true);
        sm.OnCredentialsMissing();

        Leave(time, history);
        Assert.Null(RunFor(sm, time, history, 300));
        Assert.Equal(ProximityState.LockedWaitingDeparture, sm.State);
    }

    [Fact]
    public void Perder_las_credenciales_con_el_desbloqueo_armado_lo_desarma()
    {
        var (sm, time, history) = Build(screenLocked: true);
        Leave(time, history);
        Assert.Equal(ProximityState.LockedArmed, RunFor(sm, time, history, 60)?.To);

        var change = sm.OnCredentialsMissing();

        Assert.Equal(ProximityState.LockedWaitingDeparture, change?.To);
        Assert.Equal(ProximityAction.None, change!.Action);
    }

    [Fact]
    public void Vaciar_una_clave_incorrecta_saca_de_Suspended_a_solo_bloquear()
    {
        var (sm, _, _) = Build(screenLocked: false);
        Assert.Equal(ProximityState.Suspended, sm.OnCredentialsChanged(preValidationPassed: false)?.To);

        var change = sm.OnCredentialsMissing();

        Assert.Equal(ProximityState.Unlocked, change?.To);
        Assert.Equal(ProximityAction.None, change!.Action);
    }

    [Fact]
    public void Escribir_las_credenciales_vuelve_a_permitir_armar_el_desbloqueo()
    {
        var (sm, time, history) = Build(screenLocked: true);
        sm.OnCredentialsMissing();
        Leave(time, history);
        Assert.Null(RunFor(sm, time, history, 30));

        Assert.Null(sm.OnCredentialsChanged(preValidationPassed: true));

        Leave(time, history);
        Assert.Equal(ProximityState.LockedArmed, RunFor(sm, time, history, 60)?.To);
    }

    [Fact]
    public void Un_desbloqueo_en_curso_se_abandona_al_perder_las_credenciales()
    {
        var (sm, time, history) = Build(screenLocked: true);
        Leave(time, history);
        Assert.Equal(ProximityState.LockedArmed, RunFor(sm, time, history, 60)?.To);
        time.Advance(TimeSpan.FromSeconds(5));
        var now = time.GetUtcNow();
        for (var i = 4; i >= 0; i--) history.Add(-40, now.AddSeconds(-i));
        Assert.Equal(ProximityState.UnlockPending, sm.Evaluate(history)?.To);

        Assert.Equal(ProximityState.LockedWaitingDeparture, sm.OnCredentialsMissing()?.To);
        // El resultado tardío del CP ya no cambia nada
        Assert.Null(sm.OnAuthResult(new AuthResult(true, 0)));
    }
}

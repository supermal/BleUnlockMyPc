using BleUnlockService.Config;
using BleUnlockService.Core;
using BleUnlockService.Notifications;
using BleUnlockService.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BleUnlockService.Tests;

// Bloqueos con el usuario delante (registro del 2026-11-04): aviso ocultado por Windows, reloj
// que anuncia cada 6–15 s y opción configurable del respaldo sin señal
public class FalseLockTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 9, 35, 0, TimeSpan.FromHours(1));

    private sealed class ScriptedNotifier(params PromptOutcome[] outcomes) : INotifier
    {
        private int _next;
        public int PromptCalls { get; private set; }

        public Task<PromptOutcome> PromptAsync(double snoozeMinutes, CancellationToken ct)
        {
            PromptCalls++;
            return Task.FromResult(outcomes[Math.Min(_next++, outcomes.Length - 1)]);
        }

        public Task CancelAsync() => Task.CompletedTask;
        public Task NotifyAsync(string message, CancellationToken ct) => Task.CompletedTask;
    }

    // —— A: aviso ocultado por Windows ——

    [Fact]
    public async Task Un_aviso_ocultado_por_Windows_se_repite_con_el_dialogo_y_no_bloquea()
    {
        var toast = new ScriptedNotifier(PromptOutcome.Suppressed);
        var dialog = new ScriptedNotifier(PromptOutcome.Reject);
        var notifier = new ResilientNotifier(toast, dialog, NullLogger<ResilientNotifier>.Instance);

        var outcome = await notifier.PromptAsync(5, CancellationToken.None);

        Assert.Equal(PromptOutcome.Reject, outcome);
        Assert.Equal(1, dialog.PromptCalls);
        // Solo ese aviso: el siguiente vuelve a probar el Toast
        Assert.False(notifier.UsingFallback);
    }

    [Fact]
    public async Task El_siguiente_aviso_vuelve_a_usar_el_Toast()
    {
        var toast = new ScriptedNotifier(PromptOutcome.Suppressed, PromptOutcome.Snooze);
        var dialog = new ScriptedNotifier(PromptOutcome.Timeout);
        var notifier = new ResilientNotifier(toast, dialog, NullLogger<ResilientNotifier>.Instance);

        await notifier.PromptAsync(5, CancellationToken.None);
        var second = await notifier.PromptAsync(5, CancellationToken.None);

        Assert.Equal(PromptOutcome.Snooze, second);
        Assert.Equal(2, toast.PromptCalls);
        Assert.Equal(1, dialog.PromptCalls);
    }

    [Fact]
    public void El_ayudante_puede_mandar_SUPPRESSED_y_el_servicio_lo_entiende()
    {
        var line = HelperProtocol.BuildResponse(7, PromptOutcome.Suppressed);

        Assert.True(HelperProtocol.TryParseResponse(line, out var id, out var outcome));
        Assert.Equal(7, id);
        Assert.Equal(PromptOutcome.Suppressed, outcome);
    }

    // —— B: retirar el aviso con un reloj que anuncia poco ——

    private static (ProximityStateMachine Sm, FakeTimeProvider Time, RssiHistory History) Pending()
    {
        var time = new FakeTimeProvider(T0);
        var history = new RssiHistory(TimeSpan.FromSeconds(120), time);
        var sm = new ProximityStateMachine(time, new ProximityOptions { LockThresholdDbm = -85 }, new NotificationOptions(), false);

        time.Advance(TimeSpan.FromSeconds(20));
        history.Add(-90, time.GetUtcNow().AddMilliseconds(-500));
        history.Add(-90, time.GetUtcNow());
        for (var i = 0; i < 30 && sm.State != ProximityState.PendingLock; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            sm.Evaluate(history);
        }

        Assert.Equal(ProximityState.PendingLock, sm.State);
        return (sm, time, history);
    }

    [Fact]
    public void Lecturas_fuertes_cada_diez_segundos_retiran_el_aviso()
    {
        // 09:35–09:42: −26…−35 dBm cada 6–15 s; antes el hueco de 3 s anulaba cada lote y el aviso
        // seguía en pantalla 6 minutos con el usuario al lado
        var (sm, time, history) = Pending();
        StateChange? change = null;
        for (var i = 0; i < 4 && change is null; i++)
        {
            time.Advance(TimeSpan.FromSeconds(10));
            history.Add(-30, time.GetUtcNow());
            change = sm.Evaluate(history);
        }

        Assert.Equal(ProximityState.Unlocked, change?.To);
        Assert.Equal(ProximityAction.CancelLockPrompt, change!.Action);
    }

    [Fact]
    public void El_desbloqueo_sigue_exigiendo_lecturas_seguidas()
    {
        // Lo mismo con la pantalla bloqueada y armada NO desbloquea: ahí sí se exige el hueco ≤ 3 s
        var time = new FakeTimeProvider(T0);
        var history = new RssiHistory(TimeSpan.FromSeconds(120), time);
        var sm = new ProximityStateMachine(time, new ProximityOptions(), new NotificationOptions(), true);
        time.Advance(TimeSpan.FromSeconds(20));
        history.Add(-90, time.GetUtcNow().AddMilliseconds(-500));
        history.Add(-90, time.GetUtcNow());
        for (var i = 0; i < 30 && sm.State != ProximityState.LockedArmed; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            sm.Evaluate(history);
        }
        Assert.Equal(ProximityState.LockedArmed, sm.State);

        for (var i = 0; i < 6; i++)
        {
            time.Advance(TimeSpan.FromSeconds(10));
            history.Add(-30, time.GetUtcNow());
            Assert.Null(sm.Evaluate(history));
        }
        Assert.Equal(ProximityState.LockedArmed, sm.State);
    }

    // —— C1: respaldo sin señal configurable ——

    [Theory]
    [InlineData(0, true)]
    [InlineData(30, true)]
    [InlineData(180, true)]
    [InlineData(3600, true)]
    [InlineData(29, false)]
    [InlineData(-1, false)]
    [InlineData(3601, false)]
    public void El_respaldo_sin_senal_es_cero_o_entre_30_y_3600(double seconds, bool valid)
    {
        var options = new BleUnlockOptions { Proximity = new ProximityOptions { NoSignalDepartureSeconds = seconds } };
        Assert.Equal(valid, OptionsValidator.Validate(options).IsValid);
    }

    [Fact]
    public void Por_defecto_el_respaldo_sin_senal_son_180_segundos()
        => Assert.Equal(180, new ProximityOptions().NoSignalDepartureSeconds);
}

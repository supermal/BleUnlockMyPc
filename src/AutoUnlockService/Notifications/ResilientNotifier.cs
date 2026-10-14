using AutoUnlockService.Session;

namespace AutoUnlockService.Notifications;

// Mantiene el aviso funcionando aunque el ayudante desaparezca: la degradacion es
// automatica y definitiva dentro de la vida del servicio
public sealed class ResilientNotifier(
    INotifier primary,
    INotifier fallback,
    ILogger<ResilientNotifier> log,
    TimeProvider time) : INotifier
{
    // Suelo deliberado: un aviso recien mostrado necesita una ventana legible, asi que
    // un plazo restante menor (o un plazo original menor) se amplia a este valor
    public static readonly TimeSpan MinimumFallbackTimeout = TimeSpan.FromSeconds(5);

    private int _degraded;

    public bool UsingFallback => Volatile.Read(ref _degraded) != 0;

    // Se puede invocar desde fuera (p. ej. si el ayudante no arranca o la config pide dialogo)
    public void Degrade(string reason)
    {
        if (Interlocked.Exchange(ref _degraded, 1) != 0) return;
        log.LogWarning("通知方式已降级为对话框：{Reason}", reason);
        _ = RetractPrimaryAsync();
    }

    // Retira lo que el primario tuviera pendiente (p. ej. un Toast con botones vivos)
    // para que no quede huerfano; es de mejor esfuerzo porque el primario puede estar caido
    private async Task RetractPrimaryAsync()
    {
        try { await primary.CancelAsync(); }
        catch (Exception ex) { log.LogDebug(ex, "No se pudo retirar el aviso pendiente del primario"); }
    }

    public async Task<PromptOutcome> PromptAsync(TimeSpan timeout, double snoozeMinutes, CancellationToken ct)
    {
        if (UsingFallback)
            return await fallback.PromptAsync(timeout, snoozeMinutes, ct);

        var startedAt = time.GetUtcNow();
        try
        {
            return await primary.PromptAsync(timeout, snoozeMinutes, ct);
        }
        catch (HelperUnavailableException ex)
        {
            // Un cierre del servicio no es motivo para degradar de forma permanente
            ct.ThrowIfCancellationRequested();
            Degrade(ex.Message);

            // El usuario no llego a ver nada: se vuelve a preguntar ya mismo con lo
            // que quedaba de plazo, en vez de dejarle sin aviso hasta el bloqueo
            var elapsed = time.GetUtcNow() - startedAt;
            var remaining = timeout - elapsed;
            if (remaining < MinimumFallbackTimeout) remaining = MinimumFallbackTimeout;

            return await fallback.PromptAsync(remaining, snoozeMinutes, ct);
        }
    }

    public async Task CancelAsync()
    {
        if (UsingFallback) { await fallback.CancelAsync(); return; }
        try { await primary.CancelAsync(); }
        catch (HelperUnavailableException ex)
        {
            Degrade(ex.Message);
            await fallback.CancelAsync();
        }
    }

    public async Task NotifyAsync(string message, CancellationToken ct)
    {
        if (UsingFallback) { await fallback.NotifyAsync(message, ct); return; }
        try { await primary.NotifyAsync(message, ct); }
        catch (HelperUnavailableException ex)
        {
            // El aviso (p. ej. "autodesbloqueo suspendido") no se puede perder: se reenvia por el respaldo
            ct.ThrowIfCancellationRequested();
            Degrade(ex.Message);
            await fallback.NotifyAsync(message, ct);
        }
    }
}

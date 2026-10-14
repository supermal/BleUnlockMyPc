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
    private static readonly TimeSpan MinimumFallbackTimeout = TimeSpan.FromSeconds(5);

    private int _degraded;

    public bool UsingFallback => Volatile.Read(ref _degraded) != 0;

    private INotifier Active => UsingFallback ? fallback : primary;

    // Se puede invocar desde fuera (p. ej. si el ayudante no arranca o la config pide dialogo)
    public void Degrade(string reason)
    {
        if (Interlocked.Exchange(ref _degraded, 1) == 0)
            log.LogWarning("通知方式已降级为对话框：{Reason}", reason);
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
            Degrade(ex.Message);

            // El usuario no llego a ver nada: se vuelve a preguntar ya mismo con lo
            // que quedaba de plazo, en vez de dejarle sin aviso hasta el bloqueo
            var elapsed = time.GetUtcNow() - startedAt;
            var remaining = timeout - elapsed;
            if (remaining < MinimumFallbackTimeout) remaining = MinimumFallbackTimeout;

            return await fallback.PromptAsync(remaining, snoozeMinutes, ct);
        }
    }

    public Task CancelAsync() => Active.CancelAsync();

    public Task NotifyAsync(string message, CancellationToken ct) => Active.NotifyAsync(message, ct);
}

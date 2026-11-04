using BleUnlockService.Session;
using BleUnlockService.Resources;

namespace BleUnlockService.Notifications;

// Mantiene el aviso funcionando aunque el ayudante desaparezca: la degradacion es
// automatica y definitiva dentro de la vida del servicio
public sealed class ResilientNotifier(
    INotifier primary,
    INotifier fallback,
    ILogger<ResilientNotifier> log) : INotifier
{
    private int _degraded;

    // Distingue "el orquestador retira el aviso" (se propaga) de "Degrade() retiro el aviso del
    // primario" (hay que volver a preguntar por el respaldo). Se reinicia en cada PromptAsync
    private int _cancelRequested;

    public bool UsingFallback => Volatile.Read(ref _degraded) != 0;

    // Se puede invocar desde fuera (p. ej. si el ayudante no arranca o la config pide dialogo)
    public void Degrade(string reason)
    {
        if (Interlocked.Exchange(ref _degraded, 1) != 0) return;
        log.LogWarning(ServiceStrings.Log_NotifierDegraded, reason);
        _ = RetractPrimaryAsync();
    }

    // Retira lo que el primario tuviera pendiente (p. ej. un Toast con botones vivos)
    // para que no quede huerfano; es de mejor esfuerzo porque el primario puede estar caido
    private async Task RetractPrimaryAsync()
    {
        try { await primary.CancelAsync(); }
        catch (Exception ex) { log.LogDebug(ex, "No se pudo retirar el aviso pendiente del primario"); }
    }

    public async Task<PromptOutcome> PromptAsync(double snoozeMinutes, CancellationToken ct)
    {
        if (UsingFallback)
            return await fallback.PromptAsync(snoozeMinutes, ct);

        Volatile.Write(ref _cancelRequested, 0);
        try
        {
            var outcome = await primary.PromptAsync(snoozeMinutes, ct);
            if (outcome != PromptOutcome.Suppressed) return outcome;

            // Windows ocultó el Toast (No molestar, pantalla completa…): el usuario no ha visto nada.
            // Solo este aviso pasa al diálogo, que no se ve afectado; el siguiente vuelve a probar el Toast
            log.LogWarning(ServiceStrings.Log_PromptSuppressed);
            return await fallback.PromptAsync(snoozeMinutes, ct);
        }
        catch (HelperUnavailableException ex)
        {
            // Un cierre del servicio no es motivo para degradar de forma permanente
            ct.ThrowIfCancellationRequested();
            Degrade(ex.Message);
            // El usuario no llego a ver nada: se vuelve a preguntar ya mismo por el respaldo
            return await fallback.PromptAsync(snoozeMinutes, ct);
        }
        catch (OperationCanceledException)
            when (!ct.IsCancellationRequested && UsingFallback && Volatile.Read(ref _cancelRequested) == 0)
        {
            // Degrade() retiro el aviso del primario mientras estaba pendiente y nadie mas lo cancelo:
            // el usuario ya no ve nada, asi que se pregunta por el respaldo
            log.LogWarning(ServiceStrings.Log_NotifierDegradedMidPrompt);
            return await fallback.PromptAsync(snoozeMinutes, ct);
        }
    }

    public async Task CancelAsync()
    {
        if (UsingFallback) { await fallback.CancelAsync(); return; }
        Volatile.Write(ref _cancelRequested, 1);
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

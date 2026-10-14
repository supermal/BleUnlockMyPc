using AutoUnlockService.Session;

namespace AutoUnlockService.Notifications;

// Se lanza cuando el canal del ayudante no esta disponible y hay que degradar
public sealed class HelperUnavailableException(string message) : Exception(message);

public interface INotifier
{
    /// <summary>
    /// Devuelve como respondio el usuario, o Timeout si se agoto el plazo.
    /// Lanza <see cref="HelperUnavailableException"/> solo si el usuario no llego a ver el aviso
    /// por un fallo del canal (la implementacion de respaldo nunca la lanza).
    /// </summary>
    Task<PromptOutcome> PromptAsync(TimeSpan timeout, double snoozeMinutes, CancellationToken ct);

    /// <summary>
    /// Retira el aviso pendiente si la implementacion puede. Efecto sobre un PromptAsync en curso:
    /// <list type="bullet">
    /// <item><description><c>HelperNotifier</c>: retira el Toast y el PromptAsync termina con
    /// <see cref="OperationCanceledException"/> (nunca con HelperUnavailableException ni con un resultado).</description></item>
    /// <item><description><c>WtsMessageNotifier</c>: no hace nada, el dialogo no se puede cerrar desde fuera;
    /// el PromptAsync sigue y termina con la respuesta del usuario o Timeout.</description></item>
    /// <item><description><c>ResilientNotifier</c>: delega en la implementacion activa. Si el aviso lo retira
    /// <c>Degrade()</c> y no el llamante, el PromptAsync no se cancela: vuelve a preguntar por el respaldo con el
    /// plazo restante.</description></item>
    /// </list>
    /// Quien llama a CancelAsync debe tolerar ambos finales (cancelacion o resultado normal).
    /// </summary>
    Task CancelAsync();

    // Aviso sin botones (p. ej. al entrar en estado parado)
    Task NotifyAsync(string message, CancellationToken ct);
}

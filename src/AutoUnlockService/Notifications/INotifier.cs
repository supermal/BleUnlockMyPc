using AutoUnlockService.Session;

namespace AutoUnlockService.Notifications;

// Se lanza cuando el canal del ayudante no esta disponible y hay que degradar
public sealed class HelperUnavailableException(string message) : Exception(message);

public interface INotifier
{
    // Devuelve como respondio el usuario, o Timeout si se agoto el plazo
    Task<PromptOutcome> PromptAsync(TimeSpan timeout, double snoozeMinutes, CancellationToken ct);

    // La señal volvio: retirar el aviso pendiente si la implementacion puede
    Task CancelAsync();

    // Aviso sin botones (p. ej. al entrar en estado parado)
    Task NotifyAsync(string message, CancellationToken ct);
}

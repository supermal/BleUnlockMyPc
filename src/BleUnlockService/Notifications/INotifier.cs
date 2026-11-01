using BleUnlockService.Session;

namespace BleUnlockService.Notifications;

// Se lanza cuando el canal del ayudante no esta disponible y hay que degradar
public sealed class HelperUnavailableException(string message) : Exception(message);

public interface INotifier
{
    /// <summary>
    /// 显示「即将锁屏」提示并等待用户回应。没有自动期限：等待只由用户点击、
    /// <see cref="CancelAsync"/>（信号恢复、屏幕已锁等）、调用方取消或通道故障结束。
    /// <see cref="PromptOutcome.Timeout"/> 只可能来自降级对话框自身的期限（不再锁屏）。
    /// Lanza <see cref="HelperUnavailableException"/> solo si el usuario no llego a ver el aviso
    /// por un fallo del canal (la implementacion de respaldo nunca la lanza).
    /// </summary>
    Task<PromptOutcome> PromptAsync(double snoozeMinutes, CancellationToken ct);

    /// <summary>
    /// Retira el aviso pendiente si la implementacion puede. Efecto sobre un PromptAsync en curso:
    /// <list type="bullet">
    /// <item><description><c>HelperNotifier</c>: retira el Toast y el PromptAsync termina con
    /// <see cref="OperationCanceledException"/> (nunca con HelperUnavailableException ni con un resultado).</description></item>
    /// <item><description><c>WtsMessageNotifier</c>: no hace nada, el dialogo no se puede cerrar desde fuera;
    /// el PromptAsync sigue y termina con la respuesta del usuario o Timeout.</description></item>
    /// <item><description><c>ResilientNotifier</c>: delega en la implementacion activa. Si el aviso lo retira
    /// <c>Degrade()</c> y no el llamante, el PromptAsync no se cancela: vuelve a preguntar por el respaldo.</description></item>
    /// </list>
    /// Quien llama a CancelAsync debe tolerar ambos finales (cancelacion o resultado normal).
    /// </summary>
    Task CancelAsync();

    // Aviso sin botones (p. ej. al entrar en estado parado)
    Task NotifyAsync(string message, CancellationToken ct);
}

namespace AutoUnlockService.Core;

// Presupuesto de envios de credenciales seguidos sin recibir ningun resultado del CP.
// La via lenta al bloqueo de cuenta: si LogonUI se destruye, RESULT no llega nunca y
// el bucle de timeout (UnlockPendingTimeoutSeconds) reintenta sin fin. Agotado el
// presupuesto, la orquestacion deja de enviar hasta que llegue un resultado, el
// usuario desbloquee a mano o cambie la configuracion.
// Thread-safety: lo tocan el hilo de evaluacion y los callbacks del canal
public sealed class CredentialAttemptBudget(int maxAttempts)
{
    private readonly int _maxAttempts = maxAttempts >= 1 ? maxAttempts : 1;
    private int _attempts;
    private int _capNoted;

    public bool IsExhausted => Volatile.Read(ref _attempts) >= _maxAttempts;

    // Se llama tras un envio real (SendCredentialsAsync devolvio true).
    // Devuelve true solo la primera vez que se alcanza el tope (para avisar una sola vez)
    public bool NoteAttempt()
    {
        int attempts = Interlocked.Increment(ref _attempts);
        return attempts >= _maxAttempts && Interlocked.Exchange(ref _capNoted, 1) == 0;
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _attempts, 0);
        Interlocked.Exchange(ref _capNoted, 0);
    }
}

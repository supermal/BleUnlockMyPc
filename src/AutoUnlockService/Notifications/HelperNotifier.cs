using AutoUnlockService.Session;
using AutoUnlockService.Resources;

namespace AutoUnlockService.Notifications;

// Delega el aviso en el ayudante, que es quien puede mostrar un Toast de verdad.
// Contrato con ResilientNotifier: HelperUnavailableException significa "el usuario no ha visto
// nada, usa el dialogo"; los resultados normales y la cancelacion del llamante no la lanzan
public sealed class HelperNotifier : INotifier
{
    private readonly HelperPipeServer _pipe;
    private readonly ILogger<HelperNotifier> _log;
    private readonly object _lock = new();
    private TaskCompletionSource<PromptOutcome>? _pending;
    private int _pendingId;
    private int _nextId;

    public HelperNotifier(HelperPipeServer pipe, ILogger<HelperNotifier> log)
    {
        _pipe = pipe;
        _log = log;

        // Se suscribe aqui y no en un metodo aparte: olvidar llamarlo dejaria las esperas sin romper nunca.
        // Si el ayudante muere con el aviso en pantalla, el usuario no ha visto nada:
        // hay que romper la espera para que ResilientNotifier vuelva a preguntar ya
        _pipe.ResponseReceived += OnResponse;
        _pipe.Disconnected += OnDisconnected;
    }

    public async Task<PromptOutcome> PromptAsync(double snoozeMinutes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_pipe.IsConnected) throw new HelperUnavailableException(ServiceStrings.Helper_NotConnected);

        // El ayudante descarta los PROMPT fuera de rango: se ajusta aqui para que un valor
        // grande no se convierta en un aviso que nunca aparece
        var snooze = double.IsFinite(snoozeMinutes) ? Math.Clamp(snoozeMinutes, 0, HelperProtocol.MaxSnoozeMinutes) : 0;

        var tcs = new TaskCompletionSource<PromptOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<PromptOutcome>? replaced;
        int id;
        lock (_lock)
        {
            replaced = _pending;
            id = ++_nextId;
            _pendingId = id;
            _pending = tcs;
        }
        // Un aviso nuevo sustituye al anterior: quien lo esperaba no se queda colgado
        replaced?.TrySetResult(PromptOutcome.Dismissed);

        try
        {
            // Si el ayudante cayo justo antes de registrar la espera, el evento ya paso: se comprueba aqui
            if (!_pipe.IsConnected) throw new HelperUnavailableException(ServiceStrings.Helper_DisconnectedBeforePrompt);

            if (!await _pipe.SendAsync(HelperProtocol.BuildPrompt(id, snooze), ct))
            {
                ct.ThrowIfCancellationRequested();
                throw new HelperUnavailableException(ServiceStrings.Helper_SendPromptFailed);
            }

            // Se pudo desconectar mientras se enviaba
            if (!_pipe.IsConnected) throw new HelperUnavailableException(ServiceStrings.Helper_DisconnectedWhilePrompting);

            // 改版：没有自动期限。等待只由以下之一结束：用户响应、CancelAsync、
            // 助手断开（HelperUnavailableException）或调用方取消（ct）
            return await tcs.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Si cancela el llamante hay que retirar el Toast; si fue CancelAsync, ya se envio el CANCEL
            if (ct.IsCancellationRequested) await RetractAsync(id);
            throw;
        }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_pending, tcs)) _pending = null;
            }
        }
    }

    public async Task CancelAsync()
    {
        TaskCompletionSource<PromptOutcome> tcs;
        int id;
        lock (_lock)
        {
            if (_pending is null) return;
            tcs = _pending;
            id = _pendingId;
            _pending = null;
        }

        // Retirar el aviso es un desenlace normal ("la señal volvio"), no una averia del ayudante:
        // el PromptAsync en curso termina con OperationCanceledException, que no degrada. No se
        // resuelve con un PromptOutcome porque Dismissed/Timeout son respuestas reales del usuario
        // y el consumidor podria actuar sobre ellas
        tcs.TrySetCanceled();
        await _pipe.SendAsync(HelperProtocol.BuildCancel(id), CancellationToken.None);
    }

    public async Task NotifyAsync(string message, CancellationToken ct)
    {
        if (!await _pipe.SendAsync(HelperProtocol.BuildNotify(message), ct))
        {
            ct.ThrowIfCancellationRequested();
            throw new HelperUnavailableException(ServiceStrings.Helper_SendNotifyFailed);
        }
    }

    private async Task RetractAsync(int id)
    {
        try { await _pipe.SendAsync(HelperProtocol.BuildCancel(id), CancellationToken.None); }
        catch (Exception ex) { _log.LogDebug(ex, "No se pudo retirar el aviso cancelado"); }
    }

    private void OnResponse(int id, PromptOutcome outcome)
    {
        TaskCompletionSource<PromptOutcome>? tcs;
        lock (_lock)
        {
            if (_pending is null || id != _pendingId) return;
            tcs = _pending;
            _pending = null;
        }
        tcs.TrySetResult(outcome);
    }

    private void OnDisconnected()
    {
        TaskCompletionSource<PromptOutcome>? tcs;
        lock (_lock)
        {
            if (_pending is null) return;
            tcs = _pending;
            _pending = null;
        }
        _log.LogWarning(ServiceStrings.Log_HelperLostDuringPrompt);
        tcs.TrySetException(new HelperUnavailableException(ServiceStrings.Helper_DisconnectedWithPromptPending));
    }
}

using AutoUnlockService.Session;

namespace AutoUnlockService.Notifications;

// Delega el aviso en el ayudante, que es quien puede mostrar un Toast de verdad.
// Contrato con ResilientNotifier: HelperUnavailableException significa "el usuario no ha visto
// nada, usa el dialogo"; los resultados normales y la cancelacion del llamante no la lanzan
public sealed class HelperNotifier : INotifier
{
    // Margen sobre el plazo del Toast para que el ayudante responda Timeout por si mismo; si ni
    // asi contesta, esta colgado y el usuario no esta viendo nada fiable
    private static readonly TimeSpan ResponseGrace = TimeSpan.FromSeconds(10);

    private readonly HelperPipeServer _pipe;
    private readonly ILogger<HelperNotifier> _log;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private TaskCompletionSource<PromptOutcome>? _pending;
    private int _pendingId;
    private int _nextId;

    public HelperNotifier(HelperPipeServer pipe, ILogger<HelperNotifier> log, TimeProvider? time = null)
    {
        _pipe = pipe;
        _log = log;
        _time = time ?? TimeProvider.System;

        // Se suscribe aqui y no en un metodo aparte: olvidar llamarlo dejaria las esperas sin romper nunca.
        // Si el ayudante muere a mitad de la cuenta atras, el usuario no ha visto nada:
        // hay que romper la espera para que ResilientNotifier vuelva a preguntar ya
        _pipe.ResponseReceived += OnResponse;
        _pipe.Disconnected += OnDisconnected;
    }

    public async Task<PromptOutcome> PromptAsync(TimeSpan timeout, double snoozeMinutes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_pipe.IsConnected) throw new HelperUnavailableException("助手未连接");

        // El ayudante descarta los PROMPT fuera de rango: se ajusta aqui para que un plazo
        // grande no se convierta en un aviso que nunca aparece
        var timeoutSeconds = Math.Clamp(timeout.TotalSeconds, 0, HelperProtocol.MaxTimeoutSeconds);
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
            if (!_pipe.IsConnected) throw new HelperUnavailableException("助手在发送提示前已断开");

            if (!await _pipe.SendAsync(HelperProtocol.BuildPrompt(id, timeoutSeconds, snooze), ct))
            {
                ct.ThrowIfCancellationRequested();
                throw new HelperUnavailableException("向助手发送提示指令失败");
            }

            // Se pudo desconectar mientras se enviaba
            if (!_pipe.IsConnected) throw new HelperUnavailableException("助手在发送提示时断开");

            try
            {
                return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds) + ResponseGrace, _time, ct);
            }
            catch (TimeoutException)
            {
                throw new HelperUnavailableException("助手未在预期时间内响应提示");
            }
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
            throw new HelperUnavailableException("向助手发送通知失败");
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
        _log.LogWarning("助手在提示未决时断开，改用对话框重新提示");
        tcs.TrySetException(new HelperUnavailableException("助手在提示未决时断开"));
    }
}

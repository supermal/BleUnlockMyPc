using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using AutoUnlockService.Resources;

namespace AutoUnlockService.Session;

// Servidor de \.\pipe\AutoUnlockHelper. El ayudante es el cliente y se reconecta
// cada vez que lo relanzamos
public sealed class HelperPipeServer(
    ILogger<HelperPipeServer> log,
    TimeProvider? time = null,
    string pipeName = HelperPipeServer.DefaultPipeName) : IHelperLink, IHelperCommandChannel, IAsyncDisposable
{
    public const string DefaultPipeName = "AutoUnlockHelper";

    // Plazo maximo para que el ayudante acepte una escritura; pasado esto el canal se da por muerto
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile NamedPipeServerStream? _pipe;
    private volatile StreamWriter? _writer;
    private volatile TaskCompletionSource<bool>? _lockedAck;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public bool IsConnected => _pipe?.IsConnected == true;

    // Cultura de interfaz que anuncio el ayudante en su HELLO (null si no la envio)
    public string? HelperCulture { get; private set; }

    public event Action<int>? Connected;
    public event Action? Disconnected;
    public event Action<int, PromptOutcome>? ResponseReceived;

    public void Start(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token), CancellationToken.None);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool wasConnected = false;
            try
            {
                _pipe = CreatePipe(pipeName);
                await _pipe.WaitForConnectionAsync(ct);
                wasConnected = true;
                _writer = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true };

                using var reader = new StreamReader(_pipe, Encoding.UTF8, leaveOpen: true);
                while (!ct.IsCancellationRequested && _pipe.IsConnected)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null) break;
                    Dispatch(line);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                log.LogWarning(ServiceStrings.Log_HelperPipeError, ex.Message);
            }
            finally
            {
                var pipe = _pipe;
                _pipe = null;
                _writer = null;
                if (pipe is not null) await pipe.DisposeAsync();

                // Cualquier espera pendiente debe romperse, no quedarse colgada
                _lockedAck?.TrySetResult(false);
                if (wasConnected) RaiseDisconnected();
            }

            // Si crear o aceptar falla de forma persistente no se debe girar sin descanso
            if (!wasConnected && !ct.IsCancellationRequested)
            {
                try { await Task.Delay(RetryDelay, _time, ct); }
                catch (OperationCanceledException) { }
            }
        }
    }

    private void RaiseDisconnected()
    {
        try { Disconnected?.Invoke(); }
        catch (Exception ex) { log.LogWarning(ServiceStrings.Log_HelperDisconnectEventError, ex.Message); }
    }

    private void Dispatch(string line)
    {
        var trimmed = line.Trim();

        if (HelperProtocol.TryParseHello(trimmed, out var sessionId, out var culture))
        {
            // Antes de Connected: quien lo escuche ya ve la cultura de este ayudante
            HelperCulture = culture;
            log.LogInformation(ServiceStrings.Log_HelperConnected, sessionId);
            Raise(() => Connected?.Invoke(sessionId));
            return;
        }

        if (trimmed == "LOCKED")
        {
            _lockedAck?.TrySetResult(true);
            return;
        }

        if (HelperProtocol.TryParseResponse(trimmed, out var id, out var outcome))
        {
            log.LogInformation(ServiceStrings.Log_PromptResponse, outcome);
            Raise(() => ResponseReceived?.Invoke(id, outcome));
            return;
        }

        log.LogWarning(ServiceStrings.Log_HelperPipeUnknownMessage);
    }

    // Un suscriptor que falla no debe tumbar el bucle de lectura
    private void Raise(Action action)
    {
        try { action(); }
        catch (Exception ex) { log.LogWarning(ServiceStrings.Log_HelperMessageError, ex.Message); }
    }

    public async Task<bool> SendAsync(string line, CancellationToken ct)
    {
        var writer = _writer;
        if (writer is null || !IsConnected) return false;

        await _writeLock.WaitAsync(ct);
        try
        {
            if (line == HelperProtocol.BuildLock())
                _lockedAck = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            using var timeoutCts = new CancellationTokenSource(WriteTimeout, _time);
            using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            await writer.WriteAsync(line.AsMemory(), writeCts.Token);
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ServiceStrings.Log_HelperSendFailed, ex.Message);
            // Un canal que no acepta escrituras esta muerto: cerrarlo hace que el bucle de lectura
            // salga y que se rompan las esperas pendientes
            if (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                try { _pipe?.Dispose(); } catch (ObjectDisposedException) { }
            return false;
        }
        finally { _writeLock.Release(); }
    }

    public async Task<bool> WaitForLockedAsync(TimeSpan timeout, CancellationToken ct)
    {
        var ack = _lockedAck;
        if (ack is null) return false;

        try { return await ack.Task.WaitAsync(timeout, _time, ct); }
        catch (TimeoutException) { return false; }
    }

    // SYSTEM (el servicio) y el usuario interactivo (el ayudante)
    private static NamedPipeServerStream CreatePipe(string name)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        // Synchronize es imprescindible: sin el, CreateFile del cliente con acceso de lectura/escritura es denegado
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            inBufferSize: 4096, outBufferSize: 4096, security);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_loop is not null) { try { await _loop; } catch (OperationCanceledException) { } }
        _cts?.Dispose();
        _writeLock.Dispose();
    }
}

// Lo minimo que HelperLauncher necesita saber del canal con el ayudante
public interface IHelperLink
{
    bool IsConnected { get; }
    event Action<int>? Connected;
}

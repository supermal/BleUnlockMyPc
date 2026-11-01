using System.Diagnostics;
using BleUnlockService.Resources;

namespace BleUnlockService.Session;

// Mantiene vivo el ayudante en la sesion interactiva. Pide degradar solo si el ayudante
// genuinamente no puede arrancar; que el usuario lo mate y vuelva a levantarse no cuenta
public sealed class HelperLauncher : IDisposable
{
    internal const int MaxConsecutiveFailures = 3;
    internal static readonly TimeSpan RelaunchDelay = TimeSpan.FromSeconds(5);

    // Un ayudante que arranca debe conectar en pocos segundos; si no, se cuenta como arranque fallido
    internal static readonly TimeSpan ConnectGrace = TimeSpan.FromSeconds(15);

    private readonly IHelperLink _link;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly Func<string, (UserSessionStartResult Result, int Pid)> _launch;
    private readonly Action<int> _terminate;

    // Tras degradar se sigue intentando, mas despacio: un arranque lento puntual (antivirus,
    // arranque en frio) no puede desactivar el Toast hasta reiniciar el servicio
    internal static readonly TimeSpan SlowRetryDelay = TimeSpan.FromSeconds(60);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _started;
    private int _consecutiveFailures;
    private int _helperPid;

    public HelperLauncher(HelperPipeServer pipe, ILogger<HelperLauncher> log, TimeProvider time)
        : this(pipe, log, time, launch: null, terminate: null) { }

    // Los delegados permiten sustituir las llamadas al sistema en las pruebas
    internal HelperLauncher(
        IHelperLink link, ILogger log, TimeProvider time,
        Func<string, (UserSessionStartResult Result, int Pid)>? launch, Action<int>? terminate)
    {
        _link = link;
        _log = log;
        _time = time;
        _launch = launch ?? (cmd =>
        {
            var result = UserSessionProcess.TryStartInActiveSession(cmd, log, out var pid);
            return (result, pid);
        });
        _terminate = terminate ?? TerminateProcess;
    }
    private TaskCompletionSource? _connectedSignal;

    public event Action<string>? DegradeRequested;

    public int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);

    public void Start(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _link.Connected += OnConnected;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
    }

    private void OnConnected(int sessionId)
    {
        // Solo cuenta como exito que el ayudante llegue a conectar
        Interlocked.Exchange(ref _consecutiveFailures, 0);
        _connectedSignal?.TrySetResult();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var exePath = Path.Combine(AppContext.BaseDirectory, "BleUnlockHelper.exe");
        bool degradeRaised = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_link.IsConnected)
                    await TryLaunchAsync($"\"{exePath}\"", ct);

                int failures = ConsecutiveFailures;
                if (failures == 0) degradeRaised = false;
                else if (!degradeRaised && failures >= MaxConsecutiveFailures)
                {
                    degradeRaised = true;
                    Raise(string.Format(ServiceStrings.Reason_HelperStartFailures, MaxConsecutiveFailures));
                }

                await Task.Delay(failures >= MaxConsecutiveFailures ? SlowRetryDelay : RelaunchDelay, _time, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Un error inesperado no puede matar al guardian: se registra y se reintenta
                _log.LogWarning(ServiceStrings.Log_HelperGuardError, ex.Message);
                try { await Task.Delay(RelaunchDelay, _time, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task TryLaunchAsync(string commandLine, CancellationToken ct)
    {
        // Como mucho un ayudante vivo: si la tuberia cayo pero el proceso anterior sigue ahi, se retira antes
        RetireCurrentHelper();

        // La señal se crea antes de lanzar: el ayudante puede conectar antes de que se empiece a esperar
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _connectedSignal = signal;

        var (result, pid) = _launch(commandLine);
        switch (result)
        {
            case UserSessionStartResult.NoUserSession:
                // Nadie ha iniciado sesion todavia: no es un fallo del ayudante, se espera
                _log.LogInformation(ServiceStrings.Log_NoUserSessionYet);
                return;

            case UserSessionStartResult.Failed:
                var failed = Interlocked.Increment(ref _consecutiveFailures);
                _log.LogWarning(ServiceStrings.Log_HelperStartFailed, failed);
                return;
        }

        Volatile.Write(ref _helperPid, pid);
        _log.LogInformation(ServiceStrings.Log_HelperProcessStarted);
        if (_link.IsConnected) return;

        try { await signal.Task.WaitAsync(ConnectGrace, _time, ct); }
        catch (TimeoutException)
        {
            var failures = Interlocked.Increment(ref _consecutiveFailures);
            _log.LogWarning(ServiceStrings.Log_HelperConnectTimeout, ConnectGrace.TotalSeconds, failures);

            // Se retira el proceso que no conecto para no acumular ayudantes huerfanos
            RetireCurrentHelper();
        }
    }

    private void RetireCurrentHelper()
    {
        var pid = Interlocked.Exchange(ref _helperPid, 0);
        if (pid != 0) _terminate(pid);
    }

    private void Raise(string reason)
    {
        try { DegradeRequested?.Invoke(reason); }
        catch (Exception ex) { _log.LogWarning(ServiceStrings.Log_DegradeRequestError, ex.Message); }
    }

    private void TerminateProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.ProcessName.Equals("BleUnlockHelper", StringComparison.OrdinalIgnoreCase)) p.Kill();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "No se pudo terminar el ayudante {Pid}", pid);
        }
    }

    public void Dispose()
    {
        _link.Connected -= OnConnected;
        _cts?.Cancel();
        // Se espera al bucle para no dejarlo ejecutandose tras liberar el token
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _cts?.Dispose();
    }
}

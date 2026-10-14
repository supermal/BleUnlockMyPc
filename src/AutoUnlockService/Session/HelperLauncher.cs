using System.Diagnostics;

namespace AutoUnlockService.Session;

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

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _consecutiveFailures;
    private TaskCompletionSource? _connectedSignal;

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

    public event Action<string>? DegradeRequested;

    public int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);

    public void Start(CancellationToken ct)
    {
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
        var exePath = Path.Combine(AppContext.BaseDirectory, "AutoUnlockHelper.exe");
        bool degradeRaised = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_link.IsConnected && ConsecutiveFailures < MaxConsecutiveFailures)
                    await TryLaunchAsync($"\"{exePath}\"", ct);

                if (!degradeRaised && ConsecutiveFailures >= MaxConsecutiveFailures)
                {
                    degradeRaised = true;
                    Raise($"助手连续 {MaxConsecutiveFailures} 次启动失败");
                }

                await Task.Delay(RelaunchDelay, _time, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Un error inesperado no puede matar al guardian: se registra y se reintenta
                _log.LogWarning("助手守护出错：{Message}", ex.Message);
                try { await Task.Delay(RelaunchDelay, _time, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task TryLaunchAsync(string commandLine, CancellationToken ct)
    {
        // La señal se crea antes de lanzar: el ayudante puede conectar antes de que se empiece a esperar
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _connectedSignal = signal;
        var (result, pid) = _launch(commandLine);
        switch (result)
        {
            case UserSessionStartResult.NoUserSession:
                // Nadie ha iniciado sesion todavia: no es un fallo del ayudante, se espera
                _log.LogInformation("尚无已登录的用户会话，稍后重试启动助手");
                return;

            case UserSessionStartResult.Failed:
                var failures = Interlocked.Increment(ref _consecutiveFailures);
                _log.LogWarning("启动助手失败（第 {Count} 次）", failures);
                return;
        }

        _log.LogInformation("助手进程已启动，等待连接");
        if (_link.IsConnected) return;

        try { await signal.Task.WaitAsync(ConnectGrace, _time, ct); }
        catch (TimeoutException)
        {
            var failures = Interlocked.Increment(ref _consecutiveFailures);
            _log.LogWarning("助手已启动但未在 {Seconds} 秒内连接（第 {Count} 次）", ConnectGrace.TotalSeconds, failures);

            // Se retira el proceso que no conecto para no acumular ayudantes huerfanos
            if (pid != 0) _terminate(pid);
        }
    }

    private void Raise(string reason)
    {
        try { DegradeRequested?.Invoke(reason); }
        catch (Exception ex) { _log.LogWarning("处理降级请求时出错：{Message}", ex.Message); }
    }

    private void TerminateProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.ProcessName.Equals("AutoUnlockHelper", StringComparison.OrdinalIgnoreCase)) p.Kill();
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
        _cts?.Dispose();
    }
}

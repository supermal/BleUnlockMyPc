using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Radios;
using BleUnlockService.Resources;

namespace BleUnlockService.Ble;

public sealed class BleScanner(
    ILogger<BleScanner> log,
    TimeProvider time,
    IBluetoothAdapterRestarter adapterRestarter) : IRssiSource
{
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(5);
    private readonly ScanWatchdog _watchdog = new(time);
    private Task? _watchdogTask;

    private const int MaxFastRetries = 3;
    private static readonly TimeSpan FastRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SlowRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StableRunTime = TimeSpan.FromSeconds(30);

    private BluetoothLEAdvertisementWatcher? _watcher;
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private int _consecutiveFailures;

    // Senal que OnStopped completa para despertar el bucle de reintento
    private volatile TaskCompletionSource _stoppedSignal = NewSignal();

    public event Action<AdvertisementSample>? SampleReceived;
    public event Action<ScanStatus>? StatusChanged;

    public bool IsAvailable { get; private set; }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        _runTask = Task.Run(() => RunAsync(token), CancellationToken.None);
        _watchdogTask = Task.Run(() => WatchdogLoopAsync(token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var runTask = _runTask is null || _watchdogTask is null
            ? _runTask
            : Task.WhenAll(_runTask, _watchdogTask);
        cts?.Cancel();

        // Esperamos al bucle con limite para que el apagado nunca se cuelgue
        var finished = true;
        if (runTask is not null)
        {
            try { await runTask.WaitAsync(StopTimeout); }
            catch (TimeoutException)
            {
                finished = false;
                log.LogWarning(ServiceStrings.Log_ScanLoopStopTimeout, StopTimeout.TotalSeconds);
            }
            catch (Exception ex) { log.LogError(ex, ServiceStrings.Log_ScanLoopExitError); }
        }

        // Tras cancelar, ningun arranque nuevo puede crear un watcher (ver TryStartWatcherAsync),
        // asi que este paso deja el estado definitivamente detenido
        StopWatcher();

        if (finished) cts?.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Nueva senal antes de arrancar, para no perder una parada ocurrida justo despues del Start
                var signal = NewSignal();
                _stoppedSignal = signal;

                if (await TryStartWatcherAsync(ct))
                {
                    // Si el watcher se detiene pronto cuenta como fallo consecutivo; solo se reinicia el
                    // contador cuando estuvo vivo un tiempo razonable
                    var startedAt = time.GetTimestamp();
                    await signal.Task.WaitAsync(ct);
                    if (time.GetElapsedTime(startedAt) >= StableRunTime) _consecutiveFailures = 0;
                    log.LogWarning(ServiceStrings.Log_WatcherStoppedRestarting);
                }

                if (ct.IsCancellationRequested) break;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Un fallo inesperado no debe matar el bucle de reintento
                try { log.LogError(ex, ServiceStrings.Log_ScanLoopUnexpectedError); } catch { /* el log no debe romper el bucle */ }
            }

            _consecutiveFailures++;
            var delay = _consecutiveFailures <= MaxFastRetries ? FastRetryDelay : SlowRetryDelay;
            try
            {
                log.LogWarning(ServiceStrings.Log_ScanUnavailableRetry, _consecutiveFailures, delay.TotalSeconds);
                await Task.Delay(delay, time, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                try { log.LogError(ex, ServiceStrings.Log_RetryWaitError); } catch { /* ignorado */ }
            }
        }
    }

    private async Task<bool> TryStartWatcherAsync(CancellationToken ct)
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            ct.ThrowIfCancellationRequested();
            if (adapter is null || !adapter.IsLowEnergySupported)
            {
                SetUnavailable(new ScanStatus(ScanStatus.NoAdapter, ServiceStrings.Scan_NoAdapter));
                return false;
            }

            var radio = await adapter.GetRadioAsync();
            ct.ThrowIfCancellationRequested();
            if (radio is null || radio.State != RadioState.On)
            {
                SetUnavailable(new ScanStatus(ScanStatus.RadioOff, ServiceStrings.Scan_RadioOff));
                return false;
            }

            // StopAsync cancela el token ANTES de tomar este cerrojo: si aqui el token sigue vivo,
            // el StopWatcher posterior esperara al cerrojo y detendra este watcher
            lock (_gate)
            {
                ct.ThrowIfCancellationRequested();
                StopWatcherLocked();
                var watcher = new BluetoothLEAdvertisementWatcher
                {
                    ScanningMode = BluetoothLEScanningMode.Active
                };
                watcher.Received += OnReceived;
                watcher.Stopped += OnStopped;
                _watcher = watcher;
                watcher.Start();
            }

            IsAvailable = true;
            _watchdog.NoteWatcherStarted();
            // Durante una recuperación los reinicios no la dan por terminada: solo un anuncio lo hace
            if (!_watchdog.IsStalled)
            {
                StatusChanged?.Invoke(new ScanStatus(ScanStatus.Running, ServiceStrings.Scan_Running));
                log.LogInformation(ServiceStrings.Scan_Running);
            }
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            SetUnavailable(new ScanStatus(ScanStatus.StartFailed, string.Format(ServiceStrings.Scan_StartFailed, ex.Message), ex.Message));
            return false;
        }
    }

    private void SetUnavailable(ScanStatus reason)
    {
        IsAvailable = false;
        StatusChanged?.Invoke(reason);
        log.LogError(ServiceStrings.Log_BleUnavailable, reason.Text);
    }

    private void StopWatcher()
    {
        lock (_gate) { StopWatcherLocked(); }
    }

    // Debe llamarse con _gate tomado
    private void StopWatcherLocked()
    {
        var watcher = _watcher;
        if (watcher is null) return;
        _watcher = null;
        watcher.Received -= OnReceived;
        watcher.Stopped -= OnStopped;
        try { watcher.Stop(); } catch { /* ya detenido */ }
        IsAvailable = false;
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        // Cualquier dispositivo cuenta: lo que se vigila es que el receptor funcione
        _watchdog.NoteAdvertisement();
        var isRandom = args.BluetoothAddressType == BluetoothAddressType.Random;
        SampleReceived?.Invoke(new AdvertisementSample(
            args.BluetoothAddress,
            isRandom,
            args.RawSignalStrengthInDBm,
            args.Timestamp));
    }

    private void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        IsAvailable = false;
        log.LogWarning(ServiceStrings.Log_WatcherStoppedAbnormally, args.Error);
        // Despierta el bucle de RunAsync para que reintente el arranque
        _stoppedSignal.TrySetResult();
    }

    // Perro guardián del receptor: ver ScanWatchdog
    private async Task WatchdogLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(WatchdogInterval, time, ct);
                await HandleWatchdogAsync(_watchdog.Check(IsAvailable), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                try { log.LogError(ex, ServiceStrings.Log_ScanLoopUnexpectedError); } catch { /* el log no debe romper el bucle */ }
            }
        }
    }

    private async Task HandleWatchdogAsync(WatchdogAction action, CancellationToken ct)
    {
        switch (action)
        {
            case WatchdogAction.Stalled:
                var text = string.Format(ServiceStrings.Scan_Stalled, ScanWatchdog.StallAfter.TotalSeconds);
                log.LogWarning(text);
                StatusChanged?.Invoke(new ScanStatus(ScanStatus.Stalled, text));
                RestartWatcher();
                break;

            case WatchdogAction.RestartScan:
                log.LogWarning(ServiceStrings.Log_ScanStillStalledRetrying);
                RestartWatcher();
                break;

            case WatchdogAction.RestartAdapter:
                await adapterRestarter.RestartAsync(ct);
                // Si el adaptador desapareció, el watcher ya se paró y el bucle lo rearranca cuando vuelva;
                // si no, se fuerza el rearranque para que el nuevo escaneo use el adaptador reiniciado
                RestartWatcher();
                break;

            case WatchdogAction.Failed:
                log.LogError(ServiceStrings.Scan_StalledFailed);
                StatusChanged?.Invoke(new ScanStatus(ScanStatus.StalledFailed, ServiceStrings.Scan_StalledFailed));
                break;

            case WatchdogAction.Recovered:
                log.LogInformation(ServiceStrings.Log_ScanRecovered);
                StatusChanged?.Invoke(new ScanStatus(ScanStatus.Running, ServiceStrings.Scan_Running));
                break;
        }
    }

    // Para el watcher actual y despierta a RunAsync, que crea uno nuevo tras su espera corta de reintento
    private void RestartWatcher()
    {
        StopWatcher();
        _stoppedSignal.TrySetResult();
    }
}

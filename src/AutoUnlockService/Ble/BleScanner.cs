using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Radios;

namespace AutoUnlockService.Ble;

public sealed class BleScanner(ILogger<BleScanner> log, TimeProvider time) : IRssiSource
{
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
    public event Action<string>? StatusChanged;

    public bool IsAvailable { get; private set; }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        _runTask = Task.Run(() => RunAsync(token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var runTask = _runTask;
        cts?.Cancel();

        // Esperamos al bucle con limite para que el apagado nunca se cuelgue
        var finished = true;
        if (runTask is not null)
        {
            try { await runTask.WaitAsync(StopTimeout); }
            catch (TimeoutException)
            {
                finished = false;
                log.LogWarning("BLE 扫描循环未在 {Seconds} 秒内退出", StopTimeout.TotalSeconds);
            }
            catch (Exception ex) { log.LogError(ex, "BLE 扫描循环退出时出错"); }
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
                    log.LogWarning("BLE Watcher 已停止，准备重新启动");
                }

                if (ct.IsCancellationRequested) break;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Un fallo inesperado no debe matar el bucle de reintento
                try { log.LogError(ex, "BLE 扫描循环发生意外错误"); } catch { /* el log no debe romper el bucle */ }
            }

            _consecutiveFailures++;
            var delay = _consecutiveFailures <= MaxFastRetries ? FastRetryDelay : SlowRetryDelay;
            try
            {
                log.LogWarning("BLE 扫描不可用（第 {Count} 次），{Delay} 秒后重试", _consecutiveFailures, delay.TotalSeconds);
                await Task.Delay(delay, time, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                try { log.LogError(ex, "BLE 重试等待出错"); } catch { /* ignorado */ }
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
                SetUnavailable("未找到支持 BLE 的蓝牙适配器");
                return false;
            }

            var radio = await adapter.GetRadioAsync();
            ct.ThrowIfCancellationRequested();
            if (radio is null || radio.State != RadioState.On)
            {
                SetUnavailable("蓝牙已关闭");
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
            StatusChanged?.Invoke("BLE 扫描已启动");
            log.LogInformation("BLE 扫描已启动");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            SetUnavailable($"启动 BLE 扫描失败：{ex.Message}");
            return false;
        }
    }

    private void SetUnavailable(string reason)
    {
        IsAvailable = false;
        StatusChanged?.Invoke(reason);
        log.LogError("BLE 不可用：{Reason}", reason);
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
        var isRandom = args.BluetoothAddressType == BluetoothAddressType.Random;
        SampleReceived?.Invoke(new AdvertisementSample(
            args.BluetoothAddress,
            isRandom,
            args.RawSignalStrengthInDBm,
            string.IsNullOrEmpty(args.Advertisement.LocalName) ? null : args.Advertisement.LocalName,
            args.Timestamp));
    }

    private void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        IsAvailable = false;
        log.LogWarning("BLE Watcher 异常停止：{Error}", args.Error);
        // Despierta el bucle de RunAsync para que reintente el arranque
        _stoppedSignal.TrySetResult();
    }
}

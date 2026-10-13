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
    private CancellationTokenSource? _cts;
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
        _ = RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _cts?.Cancel();
        StopWatcher();
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Nueva senal antes de arrancar, para no perder una parada ocurrida justo despues del Start
            var signal = NewSignal();
            _stoppedSignal = signal;

            if (await TryStartWatcherAsync())
            {
                // Si el watcher se detiene pronto cuenta como fallo consecutivo; solo se reinicia el
                // contador cuando estuvo vivo un tiempo razonable
                var startedAt = time.GetTimestamp();
                try { await signal.Task.WaitAsync(ct); }
                catch (OperationCanceledException) { break; }
                if (time.GetElapsedTime(startedAt) >= StableRunTime) _consecutiveFailures = 0;
                log.LogWarning("BLE Watcher 已停止，准备重新启动");
            }

            _consecutiveFailures++;
            var delay = _consecutiveFailures <= MaxFastRetries ? FastRetryDelay : SlowRetryDelay;
            log.LogWarning("BLE 扫描不可用（第 {Count} 次），{Delay} 秒后重试", _consecutiveFailures, delay.TotalSeconds);
            try { await Task.Delay(delay, time, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<bool> TryStartWatcherAsync()
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter is null || !adapter.IsLowEnergySupported)
            {
                SetUnavailable("未找到支持 BLE 的蓝牙适配器");
                return false;
            }

            var radio = await adapter.GetRadioAsync();
            if (radio is null || radio.State != RadioState.On)
            {
                SetUnavailable("蓝牙已关闭");
                return false;
            }

            StopWatcher();
            _watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };
            _watcher.Received += OnReceived;
            _watcher.Stopped += OnStopped;
            _watcher.Start();

            IsAvailable = true;
            StatusChanged?.Invoke("BLE 扫描已启动");
            log.LogInformation("BLE 扫描已启动");
            return true;
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
        if (_watcher is null) return;
        _watcher.Received -= OnReceived;
        _watcher.Stopped -= OnStopped;
        try { _watcher.Stop(); } catch { /* ya detenido */ }
        _watcher = null;
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

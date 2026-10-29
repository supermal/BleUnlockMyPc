using System.Collections.Concurrent;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

namespace AutoUnlockManager.Services.Ble;

/// <summary>
/// 电脑主动配对用的 BLE 扫描器：只收集可连接且广播了名字的外设（耳机/鼠标进入配对模式后会有名字）。
/// 事件在 WinRT 后台线程触发，调用方只置脏标记，由 UI 定时器读取 <see cref="Snapshot"/>。
/// </summary>
public sealed class BleCentralScanner : IDisposable
{
    private readonly ConcurrentDictionary<ulong, NearbyBleDevice> _devices = new();
    private BluetoothLEAdvertisementWatcher? _watcher;
    private bool _disposed;

    public string? LastError { get; private set; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();

        LastError = null;
        _devices.Clear();

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
        };
        watcher.Received += OnReceived;
        watcher.Stopped += OnStopped;
        _watcher = watcher;
        watcher.Start();
    }

    public void Stop()
    {
        var watcher = _watcher;
        if (watcher is null)
        {
            return;
        }

        _watcher = null;
        watcher.Received -= OnReceived;
        watcher.Stopped -= OnStopped;
        try
        {
            watcher.Stop();
        }
        catch
        {
            // 停止扫描失败不影响对话框关闭
        }
    }

    public IReadOnlyList<NearbyBleDevice> Snapshot()
        => _devices.Values
            .OrderByDescending(d => d.Rssi)
            .ThenBy(d => d.Name)
            .Take(50)
            .ToList();

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        // IsConnectable / BluetoothAddressType 是 Windows 10 2004 起才有的属性；
        // 旧系统退化为「有名字就显示」，地址类型按 Public 尝试配对。
        var isConnectable = true;
        var isRandomAddress = false;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            isConnectable = args.IsConnectable;
            isRandomAddress = args.BluetoothAddressType == BluetoothAddressType.Random;
        }

        if (!isConnectable)
        {
            return;
        }

        var name = args.Advertisement.LocalName;
        if (string.IsNullOrWhiteSpace(name))
        {
            return; // 未命名设备太多，用户也无法确认是哪一台
        }

        var device = new NearbyBleDevice
        {
            AddressValue = args.BluetoothAddress,
            IsRandomAddress = isRandomAddress,
            Address = BleAddressText.Format(args.BluetoothAddress),
            Name = name.Trim(),
            Rssi = args.RawSignalStrengthInDBm,
            LastSeen = args.Timestamp,
        };
        _devices[args.BluetoothAddress] = device;
    }

    private void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        if (args.Error != BluetoothError.Success)
        {
            LastError = $"扫描已停止（{args.Error}）。";
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}

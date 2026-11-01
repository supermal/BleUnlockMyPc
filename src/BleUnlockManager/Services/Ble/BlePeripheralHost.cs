using System.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Radios;
using Windows.Storage.Streams;
using BleUnlockManager.Core.Resources;

namespace BleUnlockManager.Services.Ble;

/// <summary>
/// 手机/手表配对用的临时 GATT 外设：广播标准心率服务 0x180D，心率测量特征要求加密读写，
/// 设备（iPhone/Apple Watch 等）读取或订阅时由 Windows 触发配对。
/// 参考已在实机验证的 Task19/BleHealthIrk 实现。
/// 服务提供者进程内只注册一次（重复注册同一 UUID 会失败），停止时只停广播、保留服务对象。
/// </summary>
public sealed class BlePeripheralHost
{
    private static readonly Guid HeartRateServiceUuid = new("0000180D-0000-1000-8000-00805F9B34FB");
    private static readonly Guid HeartRateMeasurementUuid = new("00002A37-0000-1000-8000-00805F9B34FB");
    private static readonly Guid BodySensorLocationUuid = new("00002A38-0000-1000-8000-00805F9B34FB");
    private static readonly Guid HeartRateControlPointUuid = new("00002A39-0000-1000-8000-00805F9B34FB");

    private static readonly Lazy<BlePeripheralHost> LazyInstance =
        new(() => new BlePeripheralHost(), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private GattServiceProvider? _provider;
    private GattServiceProviderAdvertisingParameters? _advertisingParameters;
    private bool _advertising;

    private BlePeripheralHost()
    {
    }

    public static BlePeripheralHost Shared => LazyInstance.Value;

    public bool IsAdvertising => _advertising;

    /// <summary>开始广播；失败时抛出带中文说明的异常（权限/适配器/策略/超时）。</summary>
    public async Task StartAdvertisingAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (_advertising)
            {
                return;
            }

            await EnsureCreatedLockedAsync(cancellationToken).ConfigureAwait(true);
            var provider = _provider!;

            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var lastStatus = GattServiceProviderAdvertisementStatus.Created;
            var lastError = BluetoothError.Success;
            void OnStatusChanged(GattServiceProvider sender, GattServiceProviderAdvertisementStatusChangedEventArgs args)
            {
                lastStatus = sender.AdvertisementStatus;
                lastError = args.Error;
                if (args.Status == GattServiceProviderAdvertisementStatus.Started)
                {
                    started.TrySetResult(true);
                }
                else if (args.Status == GattServiceProviderAdvertisementStatus.Aborted && _advertising)
                {
                    // Started 之前的 Aborted(Success) 是 Windows 的瞬态事件，继续等 Started；
                    // 只有广播已成功后的 Aborted 才算运行期掉线。
                    _advertising = false;
                }
            }

            provider.AdvertisementStatusChanged += OnStatusChanged;
            try
            {
                // 必须保留参数对象的托管引用：只传临时对象时广播可能立即被 GC/中止。
                _advertisingParameters ??= new GattServiceProviderAdvertisingParameters
                {
                    IsDiscoverable = true,
                    IsConnectable = true,
                };
                provider.StartAdvertising(_advertisingParameters);

                if (provider.AdvertisementStatus == GattServiceProviderAdvertisementStatus.Started)
                {
                    started.TrySetResult(true);
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
                var completed = await Task.WhenAny(started.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token))
                    .ConfigureAwait(true);

                if (completed != started.Task)
                {
                    provider.StopAdvertising();
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException(
                        string.Format(UiStrings.Ble_AdvertiseStartTimeout, lastStatus, lastError));
                }

                try
                {
                    await started.Task.ConfigureAwait(true);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch
                {
                    provider.StopAdvertising();
                    throw;
                }

                _advertising = true;
            }
            finally
            {
                provider.AdvertisementStatusChanged -= OnStatusChanged;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>停止广播（保留已注册的 GATT 服务，便于用户取消后重试）。</summary>
    public void StopAdvertising()
    {
        var provider = _provider;
        if (provider is null)
        {
            _advertising = false;
            return;
        }

        try
        {
            provider.StopAdvertising();
        }
        catch
        {
            // 关闭对话框时尽力而为；异常不应冒泡到 UI
        }
        finally
        {
            _advertising = false;
        }
    }

    private async Task EnsureCreatedLockedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_provider is not null)
        {
            return;
        }

        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter is null)
        {
            throw new InvalidOperationException(UiStrings.Ble_NoAdapter);
        }

        if (!adapter.IsLowEnergySupported)
        {
            throw new InvalidOperationException(UiStrings.Ble_NoLowEnergy);
        }

        if (!adapter.IsPeripheralRoleSupported)
        {
            throw new InvalidOperationException(
                UiStrings.Ble_NoPeripheralRole);
        }

        var radio = await adapter.GetRadioAsync();
        if (radio is not null && radio.State != RadioState.On)
        {
            throw new InvalidOperationException(UiStrings.Ble_RadioOff);
        }

        var result = await GattServiceProvider.CreateAsync(HeartRateServiceUuid);
        if (result.Error != BluetoothError.Success || result.ServiceProvider is null)
        {
            throw new InvalidOperationException(
                string.Format(UiStrings.Ble_ServiceCreateFailed, result.Error));
        }

        var provider = result.ServiceProvider;

        var measurementParams = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
            ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
            UserDescription = "Heart Rate Measurement",
        };
        var measurement = await provider.Service.CreateCharacteristicAsync(
            HeartRateMeasurementUuid, measurementParams);
        if (measurement.Error != BluetoothError.Success || measurement.Characteristic is null)
        {
            throw new InvalidOperationException(string.Format(UiStrings.Ble_CharacteristicCreateFailed, measurement.Error));
        }

        measurement.Characteristic.ReadRequested += OnReadRequested;

        // 标准心率服务的其余特征：部分手机/手表会先枚举服务，缺失时可能不显示为健康设备。
        var bodySensorParams = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Read,
            ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
            StaticValue = CreateBuffer(0x02), // Body Sensor Location: wrist
            UserDescription = "Body Sensor Location",
        };
        _ = await provider.Service.CreateCharacteristicAsync(BodySensorLocationUuid, bodySensorParams);

        var controlPointParams = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Write,
            WriteProtectionLevel = GattProtectionLevel.EncryptionRequired,
            UserDescription = "Heart Rate Control Point",
        };
        _ = await provider.Service.CreateCharacteristicAsync(HeartRateControlPointUuid, controlPointParams);

        _provider = provider;
    }

    private static async void OnReadRequested(GattLocalCharacteristic sender, GattReadRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var request = await args.GetRequestAsync();
            if (request is null)
            {
                return;
            }

            // flags=0（uint8 BPM），BPM=72
            request.RespondWithValue(CreateBuffer(0x00, 72));
        }
        catch
        {
            // 客户端断开等瞬态错误：不把异常抛到 WinRT 事件线程
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static IBuffer CreateBuffer(params byte[] bytes)
    {
        using var writer = new DataWriter();
        writer.WriteBytes(bytes);
        return writer.DetachBuffer();
    }
}

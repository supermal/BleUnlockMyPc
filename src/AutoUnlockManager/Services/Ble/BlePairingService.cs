using System.Threading;
using AutoUnlockManager.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace AutoUnlockManager.Services.Ble;

/// <summary>
/// Windows 蓝牙配对/解除配对封装。配对使用 Windows 标准配对（Pairing.PairAsync），
/// 由系统弹出自己的配对确认窗口（支持 Just Works、PIN、Passkey），而不是打开“设置”页；
/// 解除配对同时支持「设备当前不在广播」的已配对设备。
/// </summary>
public static class BlePairingService
{
    private static readonly TimeSpan PairTimeout = TimeSpan.FromSeconds(90);

    /// <summary>由电脑发起配对（Central 角色），配对确认/PIN 由管理端自己的界面完成。</summary>
    public static async Task<BlePairingResult> PairAsync(
        ulong address,
        bool isRandomAddress,
        Func<PairingPrompt, CancellationToken, Task<PairingPromptResult>>? promptAsync,
        CancellationToken cancellationToken = default)
    {
        try
        {
            LogPair($"开始配对：address={BleAddressText.Format(address)} random={isRandomAddress}");
            var addressType = isRandomAddress ? BluetoothAddressType.Random : BluetoothAddressType.Public;
            using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address, addressType);
            if (device is null)
            {
                LogPair("设备不可见");
                return BlePairingResult.Fail("设备已不可见，请确认它仍在配对模式，然后重新扫描。");
            }

            var pairing = device.DeviceInformation?.Pairing;
            if (pairing is null)
            {
                LogPair("配对接口为空");
                return BlePairingResult.Fail("无法读取设备的配对接口。");
            }

            LogPair($"设备={device.Name} IsPaired={pairing.IsPaired} CanPair={pairing.CanPair}");
            if (pairing.IsPaired)
            {
                return BlePairingResult.Ok("该设备已与 Windows 配对。");
            }

            var custom = pairing.Custom;
            async void OnPairingRequested(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs args)
            {
                var deferral = args.GetDeferral();
                try
                {
                    LogPair($"PairingRequested kind={args.PairingKind} pin={!string.IsNullOrEmpty(args.Pin)}");

                    var promptKind = args.PairingKind switch
                    {
                        DevicePairingKinds.ConfirmOnly => PairingPromptKind.ConfirmOnly,
                        DevicePairingKinds.DisplayPin => PairingPromptKind.DisplayPin,
                        DevicePairingKinds.ProvidePin => PairingPromptKind.ProvidePin,
                        DevicePairingKinds.ConfirmPinMatch => PairingPromptKind.ConfirmPinMatch,
                        _ => (PairingPromptKind?)null,
                    };

                    if (promptKind is null)
                    {
                        LogPair($"不支持的配对方式：{args.PairingKind}");
                        return;
                    }

                    PairingPromptResult response;
                    if (promptAsync is null)
                    {
                        // 没有界面时只自动接受 Just Works；其他方式不能静默配对。
                        response = promptKind == PairingPromptKind.ConfirmOnly
                            ? PairingPromptResult.Accept()
                            : PairingPromptResult.Reject();
                    }
                    else
                    {
                        response = await promptAsync(
                            new PairingPrompt(promptKind.Value, device.Name, args.Pin),
                            cancellationToken);
                    }

                    if (!response.Accepted)
                    {
                        LogPair("用户拒绝配对");
                        return;
                    }

                    if (promptKind == PairingPromptKind.ProvidePin)
                    {
                        if (string.IsNullOrWhiteSpace(response.Pin))
                        {
                            LogPair("ProvidePin 未输入 PIN");
                            return;
                        }

                        args.Accept(response.Pin.Trim());
                    }
                    else
                    {
                        args.Accept();
                    }

                    LogPair($"已接受配对：{args.PairingKind}");
                }
                catch (Exception ex)
                {
                    LogPair($"PairingRequested 异常：{ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}");
                }
                finally
                {
                    deferral.Complete();
                }
            }

            custom.PairingRequested += OnPairingRequested;
            try
            {
                using var pairCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                pairCts.CancelAfter(PairTimeout);
                DevicePairingResult result;
                try
                {
                    // 自定义配对让 Windows 把确认/PIN 事件交给本应用，由我们自己的界面处理；
                    // 桌面端不会提供系统配对弹窗。
                    result = await custom.PairAsync(
                        DevicePairingKinds.ConfirmOnly |
                        DevicePairingKinds.DisplayPin |
                        DevicePairingKinds.ProvidePin |
                        DevicePairingKinds.ConfirmPinMatch,
                        DevicePairingProtectionLevel.Default).AsTask(pairCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    LogPair("配对超时");
                    return BlePairingResult.Fail(
                        "等待配对确认超时。请重试，并在本向导的配对提示中完成确认/PIN。");
                }

                LogPair($"配对结果：{result.Status}");
                return result.Status switch
                {
                    DevicePairingResultStatus.Paired => BlePairingResult.Ok("配对成功。"),
                    DevicePairingResultStatus.AlreadyPaired => BlePairingResult.Ok("该设备已配对。"),
                    DevicePairingResultStatus.AuthenticationFailure => BlePairingResult.Fail(
                        "设备拒绝了配对，或 PIN/确认码不正确。请重试，并在本向导的配对提示中重新输入。"),
                    DevicePairingResultStatus.AuthenticationTimeout => BlePairingResult.Fail(
                        "配对确认超时。请重试，并在本向导的配对提示中及时确认。"),
                    DevicePairingResultStatus.ConnectionRejected => BlePairingResult.Fail(
                        "设备拒绝了连接。请确认设备仍处于配对模式，然后重试。"),
                    DevicePairingResultStatus.OperationAlreadyInProgress => BlePairingResult.Fail(
                        "另一个配对操作正在进行。请先完成屏幕上已有的配对窗口，再重试。"),
                    DevicePairingResultStatus.NotReadyToPair => BlePairingResult.Fail(
                        "设备尚未准备好配对。请确认它仍处于配对模式，然后重试。"),
                    DevicePairingResultStatus.AccessDenied => BlePairingResult.Fail(
                        "Windows 拒绝对该设备的配对操作。请确认管理端以管理员身份运行。"),
                    DevicePairingResultStatus.RequiredHandlerNotRegistered => BlePairingResult.Fail(
                        "设备要求的配对方式不受支持（可能需要密码/证书）。请改用设备厂商的应用。"),
                    _ => BlePairingResult.Fail($"配对失败（{result.Status}）。"),
                };
            }
            finally
            {
                custom.PairingRequested -= OnPairingRequested;
            }
        }
        catch (OperationCanceledException)
        {
            LogPair("配对已取消");
            return BlePairingResult.Fail("已取消配对。");
        }
        catch (Exception ex)
        {
            LogPair($"异常 {ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}");
            return BlePairingResult.Fail($"配对失败：{ex.Message}");
        }
    }

    private static void LogPair(string message)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            File.AppendAllText(
                Path.Combine(AppPaths.LogsDir, "manager-pair.log"),
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // 诊断日志失败不影响配对主流程
        }
    }

    /// <summary>解除 Windows 蓝牙配对（按地址查找已配对设备，不要求设备正在广播）。</summary>
    public static async Task<BlePairingResult> UnpairAsync(
        string address,
        CancellationToken cancellationToken = default)
    {
        PairedTarget? target = null;
        Log($"开始解除配对：address={address}");
        try
        {
            target = await FindPairedDeviceAsync(address, cancellationToken);
            if (target is null)
            {
                Log("未找到已配对设备");
                return BlePairingResult.Fail(
                    "Windows 的已配对设备中找不到该地址；可能已经解除，请点「刷新列表」确认。");
            }

            var pairing = target.Information.Pairing;
            if (pairing is null)
            {
                Log("配对接口为空");
                return BlePairingResult.Fail("无法读取该设备的配对接口。");
            }

            // BluetoothLEDevice.DeviceInformation 的 IsPaired 才是可靠值；
            // DeviceInformation.FindAllAsync 返回的 AssociationEndpoint 可能误报 false。
            // 兜底对象（target.Device 为空）即使 IsPaired=false 也尝试一次，由结果状态判断。
            if (target.Device is not null && !pairing.IsPaired)
            {
                Log("设备对象报告未配对");
                return BlePairingResult.Fail(
                    "Windows 报告该设备当前未配对（可能已在系统设置中解除）。请点「刷新列表」确认。");
            }

            var result = await pairing.UnpairAsync().AsTask(cancellationToken);
            Log($"解除结果：{result.Status}");
            return result.Status switch
            {
                DeviceUnpairingResultStatus.Unpaired => BlePairingResult.Ok("已解除 Windows 蓝牙配对。"),
                DeviceUnpairingResultStatus.AlreadyUnpaired => BlePairingResult.Ok("该设备已经解除过配对。"),
                _ => BlePairingResult.Fail($"解除配对失败（{result.Status}）。"),
            };
        }
        catch (OperationCanceledException)
        {
            Log("解除已取消");
            return BlePairingResult.Fail("已取消解除配对。");
        }
        catch (Exception ex)
        {
            Log($"异常 {ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}");
            return BlePairingResult.Fail($"解除配对失败：{ex.Message}");
        }
        finally
        {
            target?.Device?.Dispose();
        }
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            File.AppendAllText(
                Path.Combine(AppPaths.LogsDir, "manager-unpair.log"),
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // 诊断日志失败不影响解除配对主流程
        }
    }

    private sealed record PairedTarget(DeviceInformation Information, BluetoothLEDevice? Device);

    private static async Task<PairedTarget?> FindPairedDeviceAsync(
        string address,
        CancellationToken cancellationToken)
    {
        if (!BleAddressText.TryParse(address, out var parsed))
        {
            return null;
        }

        // 首选 BluetoothLEDevice：它返回的 DeviceInformation.Pairing.IsPaired 可靠，
        // 且设备不在广播时也能从 Windows 配对缓存取回。Random 地址类型作为兜底再试一次。
        foreach (var addressType in new[] { BluetoothAddressType.Public, BluetoothAddressType.Random })
        {
            cancellationToken.ThrowIfCancellationRequested();
            BluetoothLEDevice? device = null;
            try
            {
                device = await BluetoothLEDevice.FromBluetoothAddressAsync(parsed, addressType);
            }
            catch
            {
                // 地址类型不匹配等情况继续尝试另一种类型
            }

            if (device is null)
            {
                continue;
            }

            var info = device.DeviceInformation;
            if (info?.Pairing?.IsPaired == true)
            {
                return new PairedTarget(info, device);
            }

            device.Dispose();
        }

        // 最后兜底：按配对库选择器枚举，按 ID 中的 48 位地址匹配。
        // 该路径拿到的可能是 AssociationEndpoint（IsPaired 不可靠），只作为最后尝试。
        try
        {
            var paired = await DeviceInformation
                .FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true))
                .AsTask(cancellationToken);
            var info = paired.FirstOrDefault(i => IdContainsAddress(i.Id, parsed));
            return info is null ? null : new PairedTarget(info, null);
        }
        catch
        {
            return null;
        }
    }

    private static bool IdContainsAddress(string id, ulong address)
    {
        var hex = address.ToString("X12");
        var normalized = new string(id.Where(Uri.IsHexDigit).ToArray());
        return normalized.Contains(hex, StringComparison.OrdinalIgnoreCase);
    }
}

using System.Threading;
using BleUnlockManager.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using BleUnlockManager.Core.Resources;

namespace BleUnlockManager.Services.Ble;

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
            LogPair($"Pairing start: address={BleAddressText.Format(address)} random={isRandomAddress}");
            var addressType = isRandomAddress ? BluetoothAddressType.Random : BluetoothAddressType.Public;
            using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address, addressType);
            if (device is null)
            {
                LogPair("Device not visible");
                return BlePairingResult.Fail(UiStrings.Pair_DeviceGone);
            }

            var pairing = device.DeviceInformation?.Pairing;
            if (pairing is null)
            {
                LogPair("Pairing interface is null");
                return BlePairingResult.Fail(UiStrings.Pair_NoPairingInterface);
            }

            LogPair($"Device={device.Name} IsPaired={pairing.IsPaired} CanPair={pairing.CanPair}");
            if (pairing.IsPaired)
            {
                return BlePairingResult.Ok(UiStrings.Pair_AlreadyPairedWithWindows);
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
                        LogPair($"Unsupported pairing kind: {args.PairingKind}");
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
                        LogPair("User rejected pairing");
                        return;
                    }

                    if (promptKind == PairingPromptKind.ProvidePin)
                    {
                        if (string.IsNullOrWhiteSpace(response.Pin))
                        {
                            LogPair("ProvidePin: no PIN entered");
                            return;
                        }

                        args.Accept(response.Pin.Trim());
                    }
                    else
                    {
                        args.Accept();
                    }

                    LogPair($"Pairing accepted: {args.PairingKind}");
                }
                catch (Exception ex)
                {
                    LogPair($"PairingRequested exception: {ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}");
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
                    LogPair("Pairing timed out");
                    return BlePairingResult.Fail(
                        UiStrings.Pair_WaitTimeout);
                }

                LogPair($"Pairing result: {result.Status}");
                return result.Status switch
                {
                    DevicePairingResultStatus.Paired => BlePairingResult.Ok(UiStrings.Pair_Succeeded),
                    DevicePairingResultStatus.AlreadyPaired => BlePairingResult.Ok(UiStrings.Pair_AlreadyPaired),
                    DevicePairingResultStatus.AuthenticationFailure => BlePairingResult.Fail(
                        UiStrings.Pair_Rejected),
                    DevicePairingResultStatus.AuthenticationTimeout => BlePairingResult.Fail(
                        UiStrings.Pair_ConfirmTimeout),
                    DevicePairingResultStatus.ConnectionRejected => BlePairingResult.Fail(
                        UiStrings.Pair_ConnectionRejected),
                    DevicePairingResultStatus.OperationAlreadyInProgress => BlePairingResult.Fail(
                        UiStrings.Pair_InProgress),
                    DevicePairingResultStatus.NotReadyToPair => BlePairingResult.Fail(
                        UiStrings.Pair_NotReady),
                    DevicePairingResultStatus.AccessDenied => BlePairingResult.Fail(
                        UiStrings.Pair_AccessDenied),
                    DevicePairingResultStatus.RequiredHandlerNotRegistered => BlePairingResult.Fail(
                        UiStrings.Pair_Unsupported),
                    _ => BlePairingResult.Fail(string.Format(UiStrings.Pair_FailedStatus, result.Status)),
                };
            }
            finally
            {
                custom.PairingRequested -= OnPairingRequested;
            }
        }
        catch (OperationCanceledException)
        {
            LogPair("Pairing cancelled");
            return BlePairingResult.Fail(UiStrings.Pair_Cancelled);
        }
        catch (Exception ex)
        {
            LogPair($"Exception {ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}");
            return BlePairingResult.Fail(string.Format(UiStrings.Pair_Failed, ex.Message));
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
        Log($"Unpairing start: address={address}");
        try
        {
            target = await FindPairedDeviceAsync(address, cancellationToken);
            if (target is null)
            {
                Log("Paired device not found");
                return BlePairingResult.Fail(
                    UiStrings.Unpair_NotFound);
            }

            var pairing = target.Information.Pairing;
            if (pairing is null)
            {
                Log("Pairing interface is null");
                return BlePairingResult.Fail(UiStrings.Unpair_NoInterface);
            }

            // BluetoothLEDevice.DeviceInformation 的 IsPaired 才是可靠值；
            // DeviceInformation.FindAllAsync 返回的 AssociationEndpoint 可能误报 false。
            // 兜底对象（target.Device 为空）即使 IsPaired=false 也尝试一次，由结果状态判断。
            if (target.Device is not null && !pairing.IsPaired)
            {
                Log("Device object reports not paired");
                return BlePairingResult.Fail(
                    UiStrings.Unpair_NotPaired);
            }

            var result = await pairing.UnpairAsync().AsTask(cancellationToken);
            Log($"Unpairing result: {result.Status}");
            return result.Status switch
            {
                DeviceUnpairingResultStatus.Unpaired => BlePairingResult.Ok(UiStrings.Unpair_Succeeded),
                DeviceUnpairingResultStatus.AlreadyUnpaired => BlePairingResult.Ok(UiStrings.Unpair_AlreadyUnpaired),
                _ => BlePairingResult.Fail(string.Format(UiStrings.Unpair_FailedStatus, result.Status)),
            };
        }
        catch (OperationCanceledException)
        {
            Log("Unpairing cancelled");
            return BlePairingResult.Fail(UiStrings.Unpair_Cancelled);
        }
        catch (Exception ex)
        {
            Log($"Exception {ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}");
            return BlePairingResult.Fail(string.Format(UiStrings.Unpair_Failed, ex.Message));
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

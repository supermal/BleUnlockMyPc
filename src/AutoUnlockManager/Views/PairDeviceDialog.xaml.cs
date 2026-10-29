using System.Collections.ObjectModel;
using AutoUnlockManager.Core.Bonds;
using AutoUnlockManager.Services.Ble;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AutoUnlockManager.Views;

/// <summary>
/// 配对新设备向导（Task 22 Step 2）：
/// 方式一 = 电脑广播 0x180D，手机/手表主动配对；方式二 = 电脑扫描并发起配对（耳机/鼠标等）。
/// 配对成功后返回 <see cref="PairedDevice"/>，由配置页重新读取配对库并选中。
/// </summary>
public sealed partial class PairDeviceDialog : ContentDialog
{
    private readonly BleBondClient _bondClient = new();
    private readonly ObservableCollection<NearbyBleDevice> _nearby = [];
    private readonly DispatcherTimer _scanTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };

    private IReadOnlyList<BleBondDevice> _baseline = [];
    private HashSet<string> _baselineKeys = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private BleCentralScanner? _scanner;
    private bool _initialized;
    private bool _busy;
    private PairingPrompt? _pairingPrompt;
    private TaskCompletionSource<PairingPromptResult>? _pairingPromptTcs;

    public PairDeviceDialog()
    {
        InitializeComponent();
        DeviceList.ItemsSource = _nearby;
        MachineNameText.Text = $"电脑名称：{Environment.MachineName}（手机/手表中看到的名字可能略有不同）";
        _scanTimer.Tick += OnScanTimerTick;
        Closed += OnDialogClosed;
        _initialized = true;
    }

    /// <summary>配对成功后为服务读到的新设备；关闭/取消时为 null。</summary>
    public BleBondDevice? PairedDevice { get; private set; }

    private async void OnStartAdvertisingClicked(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            if (!await EnsureBaselineAsync(PeripheralStatusText))
            {
                return;
            }

            _cts = new CancellationTokenSource();
            StartAdvertisingButton.IsEnabled = false;
            StopAdvertisingButton.IsEnabled = true;
            PeripheralProgress.IsActive = true;
            PeripheralStatusText.Text = $"正在广播「{Environment.MachineName}」…请在手机/手表上完成配对。";

            await BlePeripheralHost.Shared.StartAdvertisingAsync(_cts.Token);
            PeripheralStatusText.Text = "广播已启动。请用手机/手表连接本机名称；配对成功后会自动读取新设备。";
            _ = PollPeripheralAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            PeripheralStatusText.Text = "已停止广播。";
            ResetPeripheralButtons();
        }
        catch (Exception ex)
        {
            PeripheralStatusText.Text = $"无法开始广播：{ex.Message}";
            ResetPeripheralButtons();
            BlePeripheralHost.Shared.StopAdvertising();
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnStopAdvertisingClicked(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        BlePeripheralHost.Shared.StopAdvertising();
        PeripheralStatusText.Text = "已停止广播。";
        ResetPeripheralButtons();
    }

    private async Task PollPeripheralAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1500), cancellationToken);
                if (DateTimeOffset.UtcNow - startedAt > TimeSpan.FromSeconds(90))
                {
                    PeripheralStatusText.Text =
                        "仍在等待配对库变化。如果手机/手表已显示配对成功但这里没有反应，" +
                        "可能是设备没有提供 IRK（无法用于自动解锁）；请关闭本窗口后点「刷新列表」查看。";
                }

                var result = await _bondClient.ListAsync(TimeSpan.FromSeconds(2), cancellationToken);
                if (result.Error is not null)
                {
                    continue;
                }

                var changed = BondDiff.FindNewOrChanged(_baseline, result.Devices);
                if (changed.Count == 0)
                {
                    continue;
                }

                PairedDevice = changed[0];
                BlePeripheralHost.Shared.StopAdvertising();
                PeripheralStatusText.Text = $"已配对：{PairedDevice.DisplayName}";
                ResetPeripheralButtons();
                await Task.Delay(600, cancellationToken);
                Hide();
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // 关闭/停止广播：正常路径
        }
        catch (Exception ex)
        {
            PeripheralStatusText.Text = $"等待配对结果失败：{ex.Message}";
            BlePeripheralHost.Shared.StopAdvertising();
            ResetPeripheralButtons();
        }
    }

    private async void OnStartScanClicked(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            if (!await EnsureBaselineAsync(CentralStatusText))
            {
                return;
            }

            _scanner?.Dispose();
            _scanner = new BleCentralScanner();
            _scanner.Start();
            _nearby.Clear();
            _scanTimer.Start();
            StartScanButton.IsEnabled = false;
            StopScanButton.IsEnabled = true;
            PairSelectedButton.IsEnabled = false;
            CentralProgress.IsActive = true;
            CentralStatusText.Text = "正在扫描可连接的 BLE 设备…请让耳机/鼠标进入配对模式。";
        }
        catch (Exception ex)
        {
            CentralStatusText.Text = $"无法开始扫描：{ex.Message}";
            StopScanning();
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnStopScanClicked(object sender, RoutedEventArgs e)
    {
        StopScanning();
        CentralStatusText.Text = "扫描已停止。可以重新开始扫描，或从上方列表选择设备。";
    }

    private async void OnPairSelectedClicked(object sender, RoutedEventArgs e)
    {
        if (_busy || DeviceList.SelectedItem is not NearbyBleDevice selected)
        {
            return;
        }

        _busy = true;
        StopScanning();
        _cts = new CancellationTokenSource();
        CentralProgress.IsActive = true;
        PairSelectedButton.IsEnabled = false;
        StartScanButton.IsEnabled = false;
        StopScanButton.IsEnabled = false;
        CentralStatusText.Text = $"正在与「{selected.Name ?? selected.Address}」配对；确认/PIN 会在本窗口弹出…";

        try
        {
            var paired = await BlePairingService.PairAsync(
                selected.AddressValue,
                selected.IsRandomAddress,
                PromptForPairingAsync,
                _cts.Token);
            if (!paired.Success)
            {
                CentralStatusText.Text = paired.Message;
                return;
            }

            CentralStatusText.Text = "配对成功，正在读取服务端的 IRK…";
            var found = await WaitForNewBondAsync(selected, _cts.Token);
            if (found is null)
            {
                CentralStatusText.Text =
                    "Windows 已配对，但服务没有读到该设备的 IRK。该设备可能不提供 IRK（无法用于自动解锁），" +
                    "请关闭本窗口后点「刷新列表」确认。";
                return;
            }

            PairedDevice = found;
            CentralStatusText.Text = $"已配对：{found.DisplayName}";
            await Task.Delay(500, _cts.Token);
            Hide();
        }
        catch (OperationCanceledException)
        {
            CentralStatusText.Text = "已取消配对。";
        }
        catch (Exception ex)
        {
            CentralStatusText.Text = $"配对失败：{ex.Message}";
        }
        finally
        {
            _busy = false;
            CentralProgress.IsActive = false;
            StartScanButton.IsEnabled = _scanner is null;
            PairSelectedButton.IsEnabled = DeviceList.SelectedItem is not null && _scanner is null;
        }
    }

    /// <summary>由 BlePairingService 从 WinRT 配对事件回调；把确认/PIN 显示在本向导内。</summary>
    private async Task<PairingPromptResult> PromptForPairingAsync(
        PairingPrompt prompt,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return PairingPromptResult.Reject();
        }

        var tcs = new TaskCompletionSource<PairingPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pairingPrompt = prompt;
        _pairingPromptTcs = tcs;

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!tcs.Task.IsCompleted)
            {
                ShowPairingPrompt(prompt);
            }
        });

        using var registration = cancellationToken.Register(
            () => tcs.TrySetResult(PairingPromptResult.Reject()));
        try
        {
            return await tcs.Task;
        }
        finally
        {
            _pairingPrompt = null;
            _pairingPromptTcs = null;
            DispatcherQueue.TryEnqueue(HidePairingPrompt);
        }
    }

    private void ShowPairingPrompt(PairingPrompt prompt)
    {
        PairingPromptTitle.Text = prompt.Kind switch
        {
            PairingPromptKind.ConfirmOnly => "允许配对？",
            PairingPromptKind.DisplayPin => "在设备上输入 PIN",
            PairingPromptKind.ProvidePin => "输入设备 PIN",
            PairingPromptKind.ConfirmPinMatch => "核对 PIN",
            _ => "配对确认",
        };

        PairingPromptMessage.Text = prompt.Kind switch
        {
            PairingPromptKind.ConfirmOnly =>
                $"设备「{prompt.DeviceName}」请求与本电脑配对。确认后由 Windows 保存配对信息，不会打开设置页。",
            PairingPromptKind.DisplayPin =>
                $"请在设备「{prompt.DeviceName}」上输入下面显示的 PIN，完成后点「已在设备输入」。",
            PairingPromptKind.ProvidePin =>
                $"请输入设备「{prompt.DeviceName}」上显示的 PIN。",
            PairingPromptKind.ConfirmPinMatch =>
                $"设备「{prompt.DeviceName}」显示的 PIN 如下。请核对设备屏幕，是否一致？",
            _ => "请完成配对确认。",
        };

        var hasPin = !string.IsNullOrWhiteSpace(prompt.Pin);
        var showPin = hasPin &&
                      prompt.Kind is PairingPromptKind.DisplayPin or PairingPromptKind.ConfirmPinMatch;
        PairingPinDisplay.Text = hasPin ? prompt.Pin : string.Empty;
        PairingPinDisplay.Visibility = showPin ? Visibility.Visible : Visibility.Collapsed;

        PairingPinBox.Visibility = prompt.Kind == PairingPromptKind.ProvidePin
            ? Visibility.Visible
            : Visibility.Collapsed;
        PairingPinBox.Text = string.Empty;
        PairingPromptError.Visibility = Visibility.Collapsed;

        PairingAcceptButton.Content = prompt.Kind switch
        {
            PairingPromptKind.ConfirmOnly => "配对",
            PairingPromptKind.DisplayPin => "已在设备输入",
            PairingPromptKind.ProvidePin => "配对",
            PairingPromptKind.ConfirmPinMatch => "一致",
            _ => "确定",
        };
        PairingRejectButton.Content = prompt.Kind == PairingPromptKind.ConfirmPinMatch
            ? "不一致"
            : "取消";

        PairingPromptOverlay.Visibility = Visibility.Visible;
        if (prompt.Kind == PairingPromptKind.ProvidePin)
        {
            PairingPinBox.Focus(FocusState.Programmatic);
        }
    }

    private void HidePairingPrompt()
    {
        PairingPromptOverlay.Visibility = Visibility.Collapsed;
        PairingPinBox.Text = string.Empty;
        PairingPromptError.Visibility = Visibility.Collapsed;
    }

    private void OnPairingAcceptClicked(object sender, RoutedEventArgs e)
    {
        var tcs = _pairingPromptTcs;
        var prompt = _pairingPrompt;
        if (tcs is null || prompt is null)
        {
            return;
        }

        if (prompt.Kind == PairingPromptKind.ProvidePin)
        {
            var pin = PairingPinBox.Text.Trim();
            if (pin.Length == 0)
            {
                PairingPromptError.Text = "请输入设备上显示的 PIN。";
                PairingPromptError.Visibility = Visibility.Visible;
                PairingPinBox.Focus(FocusState.Programmatic);
                return;
            }

            tcs.TrySetResult(PairingPromptResult.Accept(pin));
            return;
        }

        tcs.TrySetResult(PairingPromptResult.Accept());
    }

    private void OnPairingRejectClicked(object sender, RoutedEventArgs e)
        => _pairingPromptTcs?.TrySetResult(PairingPromptResult.Reject());

    private async Task<BleBondDevice?> WaitForNewBondAsync(
        NearbyBleDevice selected,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(12);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await _bondClient.ListAsync(TimeSpan.FromSeconds(2), cancellationToken);
            if (result.Error is null)
            {
                var changed = BondDiff.FindNewOrChanged(_baseline, result.Devices);
                var hit = changed.FirstOrDefault(d =>
                              string.Equals(BondDiff.AddressKey(d.Address),
                                  BondDiff.AddressKey(selected.Address), StringComparison.OrdinalIgnoreCase))
                          ?? changed.FirstOrDefault()
                          ?? BondDiff.FindByAddress(result.Devices, selected.Address);
                if (hit is not null && hit.Irk.Length > 0)
                {
                    return hit;
                }
            }

            await Task.Delay(1000, cancellationToken);
        }

        return null;
    }

    private async Task<bool> EnsureBaselineAsync(TextBlock status)
    {
        status.Text = "正在读取 Windows 配对库…";
        var result = await _bondClient.ListAsync();
        if (result.Error is not null)
        {
            status.Text = $"无法读取 Windows 配对库：{result.Error} 请先在管理端启动服务。";
            return false;
        }

        _baseline = result.Devices;
        _baselineKeys = result.Devices
            .Select(d => BondDiff.AddressKey(d.Address))
            .Where(k => k.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return true;
    }

    private void OnScanTimerTick(object? sender, object e)
    {
        if (_scanner is null)
        {
            return;
        }

        if (_scanner.LastError is { Length: > 0 } error)
        {
            CentralStatusText.Text = error;
            StopScanning();
            return;
        }

        var selectedAddress = (DeviceList.SelectedItem as NearbyBleDevice)?.AddressValue;
        var devices = _scanner.Snapshot()
            .Where(d => !_baselineKeys.Contains(BondDiff.AddressKey(d.Address)))
            .ToList();

        if (_nearby.Count == devices.Count &&
            _nearby.Zip(devices).All(pair =>
                pair.First.AddressValue == pair.Second.AddressValue &&
                pair.First.Rssi == pair.Second.Rssi))
        {
            return;
        }

        _nearby.Clear();
        foreach (var device in devices)
        {
            _nearby.Add(device);
        }

        if (selectedAddress is { } address)
        {
            DeviceList.SelectedItem = _nearby.FirstOrDefault(d => d.AddressValue == address);
        }

        PairSelectedButton.IsEnabled = DeviceList.SelectedItem is not null && _scanner is not null;
        CentralStatusText.Text = devices.Count == 0
            ? "正在扫描…暂未发现带名字的可连接设备；请确认设备已进入配对模式。"
            : $"发现 {devices.Count} 台可连接设备；选择后点「配对所选设备」。";
    }

    private void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
        => PairSelectedButton.IsEnabled = DeviceList.SelectedItem is not null && _scanner is not null && !_busy;

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        var peripheral = PeripheralModeRadio.IsChecked == true;
        PeripheralPanel.Visibility = peripheral ? Visibility.Visible : Visibility.Collapsed;
        CentralPanel.Visibility = peripheral ? Visibility.Collapsed : Visibility.Visible;
        CancelCurrentOperation();
    }

    private void CancelCurrentOperation()
    {
        _cts?.Cancel();
        _pairingPromptTcs?.TrySetResult(PairingPromptResult.Reject());
        HidePairingPrompt();
        BlePeripheralHost.Shared.StopAdvertising();
        StopScanning();
        ResetPeripheralButtons();
        CentralProgress.IsActive = false;
        PairSelectedButton.IsEnabled = false;
        StartScanButton.IsEnabled = true;
    }

    private void StopScanning()
    {
        _scanTimer.Stop();
        _scanner?.Stop();
        _scanner?.Dispose();
        _scanner = null;
        StartScanButton.IsEnabled = true;
        StopScanButton.IsEnabled = false;
        PairSelectedButton.IsEnabled = false;
        CentralProgress.IsActive = false;
    }

    private void ResetPeripheralButtons()
    {
        StartAdvertisingButton.IsEnabled = true;
        StopAdvertisingButton.IsEnabled = false;
        PeripheralProgress.IsActive = false;
    }

    private void OnDialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        _cts?.Cancel();
        _pairingPromptTcs?.TrySetResult(PairingPromptResult.Reject());
        HidePairingPrompt();
        _scanTimer.Stop();
        BlePeripheralHost.Shared.StopAdvertising();
        _scanner?.Dispose();
        _scanner = null;
    }
}

using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AutoUnlockManager.Core.Bonds;
using AutoUnlockManager.Services.Ble;

namespace AutoUnlockManager.Views;

/// <summary>
/// Asistente de emparejamiento, mostrado como capa modal dentro de la ventana principal.
/// Dos modos: periférico (el teléfono o el reloj inicia la conexión) y central (el PC escanea).
/// La lógica de emparejamiento es la de la versión WinUI; aquí solo cambia la presentación.
/// </summary>
public sealed partial class PairDeviceDialog : UserControl
{
    private static readonly TimeSpan PeripheralWaitLimit = TimeSpan.FromSeconds(90);

    private readonly BleBondClient _bondClient = new();
    private readonly ObservableCollection<NearbyBleDevice> _nearby = [];
    private readonly DispatcherTimer _scanTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private readonly DispatcherTimer _elapsedTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IReadOnlyList<BleBondDevice> _baseline = [];
    private HashSet<string> _baselineKeys = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private BleCentralScanner? _scanner;
    private DateTimeOffset _advertisingSince;
    private bool _initialized;
    private bool _busy;
    private bool _advertising;
    private PairingPrompt? _pairingPrompt;
    private TaskCompletionSource<PairingPromptResult>? _pairingPromptTcs;

    public PairDeviceDialog()
    {
        InitializeComponent();
        DeviceList.ItemsSource = _nearby;
        MachineNameText.Text = $"电脑名称：{Environment.MachineName}（手机/手表中看到的名字可能略有不同）";
        _scanTimer.Tick += OnScanTimerTick;
        _elapsedTimer.Tick += (_, _) => UpdateElapsed();
        _initialized = true;
        SetPeripheralStep(1);
        UpdateFooter();
        Loaded += (_, _) => PeripheralModeRadio.Focus();
    }

    /// <summary>Dispositivo emparejado con éxito (null si se cerró sin emparejar).</summary>
    public BleBondDevice? PairedDevice { get; private set; }

    /// <summary>Se completa cuando el diálogo se cierra.</summary>
    public Task Completion => _closed.Task;

    public void Close()
    {
        if (_closed.Task.IsCompleted)
        {
            return;
        }

        _cts?.Cancel();
        _pairingPromptTcs?.TrySetResult(PairingPromptResult.Reject());
        HidePairingPrompt();
        _scanTimer.Stop();
        _elapsedTimer.Stop();
        BlePeripheralHost.Shared.StopAdvertising();
        _scanner?.Dispose();
        _scanner = null;
        _closed.TrySetResult();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        // Esc primero retira la confirmación de PIN; con ella cerrada, cierra el diálogo
        if (PairingPromptOverlay.Visibility == Visibility.Visible)
        {
            _pairingPromptTcs?.TrySetResult(PairingPromptResult.Reject());
        }
        else
        {
            Close();
        }

        e.Handled = true;
    }

    // ===== Modo periférico =====

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
            _advertising = true;
            _advertisingSince = DateTimeOffset.UtcNow;
            SetPeripheralBusy(true);
            UpdateFooter();
            PeripheralStatusText.Text = $"正在广播「{Environment.MachineName}」，等待设备连接…";

            await BlePeripheralHost.Shared.StartAdvertisingAsync(_cts.Token);
            SetPeripheralStep(3);
            _ = PollPeripheralAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            PeripheralStatusText.Text = "已停止广播。";
            ResetPeripheral();
        }
        catch (Exception ex)
        {
            PeripheralStatusText.Text = $"无法开始广播：{ex.Message}";
            ResetPeripheral();
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
        ResetPeripheral();
    }

    private async Task PollPeripheralAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1500), cancellationToken);
                if (DateTimeOffset.UtcNow - _advertisingSince > PeripheralWaitLimit)
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
                SetPeripheralStep(5);
                ResetPeripheral(keepStep: true);
                PeripheralStatusText.Text = $"已配对：{PairedDevice.DisplayName}";
                await Task.Delay(600, cancellationToken);
                Close();
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // Cerrado o detenido por el usuario
        }
        catch (Exception ex)
        {
            PeripheralStatusText.Text = $"等待配对结果失败：{ex.Message}";
            BlePeripheralHost.Shared.StopAdvertising();
            ResetPeripheral();
        }
    }

    /// <summary>Paso actual 1–4; 5 = todos hechos.</summary>
    private void SetPeripheralStep(int current)
    {
        var steps = new[] { (Step1, Step1Text), (Step2, Step2Text), (Step3, Step3Text), (Step4, Step4Text) };
        for (var i = 0; i < steps.Length; i++)
        {
            var number = i + 1;
            var (badge, text) = steps[i];
            badge.Tag = number < current ? "done" : number == current ? "now" : null;
            text.Text = number < current ? "✓" : number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private void SetPeripheralBusy(bool busy)
    {
        PeripheralProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        PeripheralStatusBox.SetResourceReference(Border.BackgroundProperty, busy ? "Ui.Status.Info.Bg" : "Ui.Bg.Layer");
        PeripheralStatusText.SetResourceReference(TextBlock.ForegroundProperty, busy ? "Ui.Status.Info.Fg" : "Ui.Text.Primary");
        ElapsedText.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Status.Info.Fg");
        if (busy)
        {
            UpdateElapsed();
            _elapsedTimer.Start();
        }
        else
        {
            _elapsedTimer.Stop();
            ElapsedText.Text = "";
        }
    }

    private void UpdateElapsed()
    {
        var elapsed = DateTimeOffset.UtcNow - _advertisingSince;
        ElapsedText.Text = $"已等待 {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
    }

    private void ResetPeripheral(bool keepStep = false)
    {
        _advertising = false;
        SetPeripheralBusy(false);
        if (!keepStep)
        {
            SetPeripheralStep(1);
        }

        UpdateFooter();
    }

    // ===== Modo central =====

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
            CentralProgress.Visibility = Visibility.Visible;
            CentralStatusText.Text = "正在扫描附近的设备…请让耳机/鼠标进入配对模式。";
        }
        catch (Exception ex)
        {
            CentralStatusText.Text = $"无法开始扫描：{ex.Message}";
            StopScanning();
        }
        finally
        {
            _busy = false;
            UpdateFooter();
        }
    }

    private void OnStopScanClicked(object sender, RoutedEventArgs e)
    {
        StopScanning();
        CentralStatusText.Text = "扫描已停止。可以重新开始扫描，或从列表选择设备。";
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
        CentralProgress.Visibility = Visibility.Visible;
        UpdateFooter();
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
            Close();
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
            CentralProgress.Visibility = Visibility.Collapsed;
            UpdateFooter();
        }
    }

    private async Task<PairingPromptResult> PromptForPairingAsync(PairingPrompt prompt, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return PairingPromptResult.Reject();
        }

        var tcs = new TaskCompletionSource<PairingPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pairingPrompt = prompt;
        _pairingPromptTcs = tcs;

        // La petición puede llegar en un hilo de WinRT
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!tcs.Task.IsCompleted)
            {
                ShowPairingPrompt(prompt);
            }
        });

        using var registration = cancellationToken.Register(() => tcs.TrySetResult(PairingPromptResult.Reject()));
        try
        {
            return await tcs.Task;
        }
        finally
        {
            _pairingPrompt = null;
            _pairingPromptTcs = null;
            _ = Dispatcher.BeginInvoke(HidePairingPrompt);
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
            PairingPromptKind.DisplayPin => $"请在设备「{prompt.DeviceName}」上输入下面显示的 PIN，完成后点「已在设备输入」。",
            PairingPromptKind.ProvidePin => $"请输入设备「{prompt.DeviceName}」上显示的 PIN。",
            PairingPromptKind.ConfirmPinMatch => $"设备「{prompt.DeviceName}」显示的 PIN 如下。请核对设备屏幕，是否一致？",
            _ => "请完成配对确认。",
        };

        var hasPin = !string.IsNullOrWhiteSpace(prompt.Pin);
        var showPin = hasPin && prompt.Kind is PairingPromptKind.DisplayPin or PairingPromptKind.ConfirmPinMatch;
        PairingPinDisplay.Text = hasPin ? prompt.Pin : string.Empty;
        PairingPinDisplay.Visibility = showPin ? Visibility.Visible : Visibility.Collapsed;
        PairingPinBox.Visibility = prompt.Kind == PairingPromptKind.ProvidePin ? Visibility.Visible : Visibility.Collapsed;
        PairingPinBox.Text = string.Empty;
        PairingPromptError.Visibility = Visibility.Collapsed;
        PairingAcceptButton.Content = prompt.Kind switch
        {
            PairingPromptKind.DisplayPin => "已在设备输入",
            PairingPromptKind.ConfirmPinMatch => "一致",
            _ => "配对",
        };
        PairingRejectButton.Content = prompt.Kind == PairingPromptKind.ConfirmPinMatch ? "不一致" : "取消";
        PairingPromptOverlay.Visibility = Visibility.Visible;

        if (prompt.Kind == PairingPromptKind.ProvidePin)
        {
            PairingPinBox.Focus();
        }
        else
        {
            PairingAcceptButton.Focus();
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
                PairingPinBox.Focus();
                return;
            }

            tcs.TrySetResult(PairingPromptResult.Accept(pin));
            return;
        }

        tcs.TrySetResult(PairingPromptResult.Accept());
    }

    private void OnPairingRejectClicked(object sender, RoutedEventArgs e) =>
        _pairingPromptTcs?.TrySetResult(PairingPromptResult.Reject());

    private async Task<BleBondDevice?> WaitForNewBondAsync(NearbyBleDevice selected, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(12);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await _bondClient.ListAsync(TimeSpan.FromSeconds(2), cancellationToken);
            if (result.Error is null)
            {
                var changed = BondDiff.FindNewOrChanged(_baseline, result.Devices);
                var hit = changed.FirstOrDefault(d =>
                              string.Equals(BondDiff.AddressKey(d.Address), BondDiff.AddressKey(selected.Address),
                                  StringComparison.OrdinalIgnoreCase))
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
            status.Text = $"无法读取 Windows 配对库：{result.Error} 请先启动服务。";
            return false;
        }

        _baseline = result.Devices;
        _baselineKeys = result.Devices
            .Select(d => BondDiff.AddressKey(d.Address))
            .Where(k => k.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return true;
    }

    private void OnScanTimerTick(object? sender, EventArgs e)
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
                pair.First.AddressValue == pair.Second.AddressValue && pair.First.Rssi == pair.Second.Rssi))
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

        CentralStatusText.Text = devices.Count == 0
            ? "正在扫描…暂未发现带名字的可连接设备；请确认设备已进入配对模式。"
            : $"发现 {devices.Count} 台可连接设备；选择后点「配对所选设备」。";
        UpdateFooter();
    }

    private void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateFooter();

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
        ResetPeripheral();
    }

    private void StopScanning()
    {
        _scanTimer.Stop();
        _scanner?.Stop();
        _scanner?.Dispose();
        _scanner = null;
        CentralProgress.Visibility = Visibility.Collapsed;
        UpdateFooter();
    }

    /// <summary>Botones del pie según el modo y el estado (solo un botón principal visible).</summary>
    private void UpdateFooter()
    {
        if (!_initialized)
        {
            return;
        }

        var peripheral = PeripheralModeRadio.IsChecked == true;
        var scanning = _scanner is not null;

        StartAdvertisingButton.Visibility = Show(peripheral && !_advertising);
        StopAdvertisingButton.Visibility = Show(peripheral && _advertising);
        StartScanButton.Visibility = Show(!peripheral && !scanning);
        StopScanButton.Visibility = Show(!peripheral && scanning);
        PairSelectedButton.Visibility = Show(!peripheral);
        PairSelectedButton.IsEnabled = !_busy && DeviceList.SelectedItem is not null;
        StartScanButton.IsEnabled = !_busy;
        StartAdvertisingButton.IsEnabled = !_busy;
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Input;
using BleUnlockManager.Core.Bonds;
using BleUnlockManager.Core.Config;
using BleUnlockManager.Core.Resources;
using BleUnlockManager.Services;
using BleUnlockManager.ViewModels;
using Wpf.Ui.Controls;

namespace BleUnlockManager.Views;

/// <summary>Fila de la lista de dispositivos emparejados del asistente.</summary>
public sealed record SetupWizardDevice(BleBondDevice Device)
{
    public string Title => string.IsNullOrWhiteSpace(Device.Name) ? Device.Address : Device.Name;

    public string Address => Device.Address;

    public bool IsSupported => Device.IsSupported;

    public string Note => IsSupported ? "" : UiStrings.Wizard_Unsupported;
}

/// <summary>
/// Asistente de primera configuración, como capa modal en la ventana principal: dispositivo → cuenta de
/// Windows → guardar. Un equipo recién instalado no tiene ni lo uno ni lo otro; el servicio ya funciona
/// en estado «sin configurar» y sirve la lista de emparejados que necesita el paso 1.
/// El guardado es el de la página Configuración (validación, cifrado DPAPI y espera de la recarga).
/// </summary>
public sealed partial class SetupWizardDialog : UserControl
{
    private readonly MainWindow _window;
    private readonly ConfigViewModel _config;
    private readonly BleBondClient _bondClient = new();
    private readonly ObservableCollection<SetupWizardDevice> _devices = [];
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _step;
    private bool _busy;
    private bool _loadingDevices;

    public SetupWizardDialog(MainWindow window, ConfigViewModel config)
    {
        _window = window;
        _config = config;
        InitializeComponent();
        DeviceList.ItemsSource = _devices;

        // Se parte siempre del disco: el asistente no hereda ediciones a medias de la página Configuración
        _config.Initialize();
        // Con dominio incluido (o «MicrosoftAccount\correo»): el nombre suelto no basta para una cuenta de dominio
        UserNameBox.Text = _config.SavedUserName.Length > 0 ? _config.SavedUserName : CurrentAccount.DefaultLogonName();

        ShowStep(1);
        Loaded += async (_, _) =>
        {
            DeviceList.Focus();
            await LoadDevicesAsync();
        };
    }

    /// <summary>Se completa al cerrarse el asistente.</summary>
    public Task Completion => _closed.Task;

    /// <summary>true si se llegó a guardar con éxito; false si se omitió o se cerró antes.</summary>
    public bool Completed { get; private set; }

    private SetupWizardDevice? SelectedDevice => DeviceList.SelectedItem as SetupWizardDevice;

    private void Close()
    {
        if (_busy)
        {
            return;
        }

        _closed.TrySetResult();
    }

    private void OnSkipClicked(object sender, RoutedEventArgs e) => Close();

    private void OnFinishClicked(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    // ===== Navegación =====

    private void ShowStep(int step)
    {
        _step = step;
        DevicePanel.Visibility = Show(step == 1);
        CredentialsPanel.Visibility = Show(step == 2);
        ResultPanel.Visibility = Show(step == 3);

        var name = step switch
        {
            1 => UiStrings.Wizard_Step1Name,
            2 => UiStrings.Wizard_Step2Name,
            _ => UiStrings.Wizard_Step3Name,
        };
        StepText.Text = string.Format(UiStrings.Wizard_StepOf, step, name);

        if (step == 2)
        {
            // Sin contraseña guardada, el campo vacío no sirve: o se escribe, o se elige «solo bloquear»
            PasswordBox.PlaceholderText = _config.PasswordPlaceholder;
            CredentialsError.Visibility = Visibility.Collapsed;
            Dispatcher.BeginInvoke(() => (UserNameBox.Text.Length == 0 ? (Control)UserNameBox : PasswordBox).Focus());
        }

        UpdateFooter();
    }

    private void UpdateFooter()
    {
        SkipButton.Visibility = Show(_step < 3);
        CloseButton.IsEnabled = !_busy;
        BackButton.Visibility = Show(_step == 2);
        SkipPasswordButton.Visibility = Show(_step == 2 && !_config.HasStoredPassword);
        NextButton.Visibility = Show(_step == 1);
        NextButton.IsEnabled = SelectedDevice is { IsSupported: true };
        SaveButton.Visibility = Show(_step == 2);
        BackToFixButton.Visibility = Show(_step == 3 && !_busy && !Completed);
        FinishButton.Visibility = Show(_step == 3 && !_busy);
    }

    private void OnNextClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is { IsSupported: true })
        {
            ShowStep(2);
        }
    }

    private void OnBackClicked(object sender, RoutedEventArgs e) => ShowStep(1);

    private void OnBackToFixClicked(object sender, RoutedEventArgs e) => ShowStep(2);

    // ===== Paso 1: dispositivo =====

    private async Task LoadDevicesAsync(string? selectAddress = null)
    {
        if (_loadingDevices)
        {
            return;
        }

        _loadingDevices = true;
        DevicesProgress.Visibility = Visibility.Visible;
        DevicesStatusText.Text = UiStrings.Wizard_ReadingDevices;
        try
        {
            // El servicio puede estar arrancando todavía (inicio automático retrasado): se reintenta un poco
            BleBondListResult result = await _bondClient.ListAsync();
            for (var attempt = 0; result.Error is not null && attempt < 5; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                result = await _bondClient.ListAsync();
            }

            var previous = selectAddress ?? SelectedDevice?.Address;
            _devices.Clear();
            if (result.Error is not null)
            {
                DevicesStatusText.Text = string.Format(UiStrings.Wizard_DevicesReadFailed, result.Error);
                return;
            }

            // Los compatibles primero; dentro de cada grupo se mantiene el orden del servicio (por nombre)
            foreach (var device in result.Devices.OrderBy(d => d.IsSupported ? 0 : 1))
            {
                _devices.Add(new SetupWizardDevice(device));
            }

            var usable = _devices.Count(d => d.IsSupported);
            DevicesStatusText.Text = usable == 0
                ? UiStrings.Wizard_NoDevices
                : string.Format(UiStrings.Wizard_DevicesFound, usable);

            // Selección: la pedida (recién emparejado), la anterior, el dispositivo ya configurado o el único posible
            var configuredIrk = ConfigValidator.NormalizeIrk(_config.IrkText);
            DeviceList.SelectedItem =
                FindSupported(previous) ??
                _devices.FirstOrDefault(d => d.IsSupported && ConfigValidator.NormalizeIrk(d.Device.Irk) == configuredIrk) ??
                (usable == 1 ? _devices.First(d => d.IsSupported) : null);
        }
        catch (Exception ex)
        {
            DevicesStatusText.Text = string.Format(UiStrings.Wizard_DevicesReadFailed, ex.Message);
        }
        finally
        {
            _loadingDevices = false;
            DevicesProgress.Visibility = Visibility.Collapsed;
            UpdateFooter();
        }
    }

    private SetupWizardDevice? FindSupported(string? address)
    {
        if (address is null)
        {
            return null;
        }

        var key = BondDiff.AddressKey(address);
        return _devices.FirstOrDefault(d =>
            d.IsSupported && string.Equals(BondDiff.AddressKey(d.Address), key, StringComparison.OrdinalIgnoreCase));
    }

    private void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateFooter();

    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await LoadDevicesAsync();

    private async void OnPairNewClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new PairDeviceDialog();
            await _window.ShowOverlayAsync(dialog, dialog.Completion);
            await LoadDevicesAsync(dialog.PairedDevice?.Address);
        }
        catch (Exception ex)
        {
            DevicesStatusText.Text = string.Format(UiStrings.Pair_Failed, ex.Message);
        }
    }

    private void OnOpenBluetoothSettingsClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "ms-settings:bluetooth", UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DevicesStatusText.Text = ex.Message;
        }
    }

    private void OnHelpToggleClicked(object sender, RoutedEventArgs e)
    {
        var open = HelpPanel.Visibility != Visibility.Visible;
        HelpPanel.Visibility = Show(open);
        HelpChevron.Symbol = open ? SymbolRegular.ChevronDown16 : SymbolRegular.ChevronRight16;
    }

    // ===== Paso 2: cuenta de Windows =====

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        var userName = UserNameBox.Text.Trim();
        var password = PasswordBox.Password;
        if (userName.Length == 0)
        {
            ShowCredentialsError(UiStrings.Wizard_UserNameRequired);
            return;
        }

        if (password.Length == 0 && !_config.HasStoredPassword)
        {
            ShowCredentialsError(UiStrings.Wizard_PasswordRequired);
            return;
        }

        // Otra cuenta, o una que no existe en el equipo, nunca desbloqueará esta sesión: se avisa una
        // vez y un segundo «保存» con el mismo usuario confirma
        if (!string.Equals(_confirmedUserName, userName, StringComparison.OrdinalIgnoreCase))
        {
            var match = await Task.Run(() => CurrentAccount.Check(userName));
            if (AccountCheck.Warning(userName, match) is { } warning)
            {
                _confirmedUserName = userName;
                ShowCredentialsError(warning + " " + UiStrings.Wizard_SaveAgainToConfirm);
                return;
            }
        }

        // Campo vacío con una contraseña guardada = conservarla
        await SaveAsync(userName, password.Length > 0 ? password : null);
    }

    // Usuario cuyo aviso de cuenta ya se mostró: el siguiente «保存» con él guarda sin volver a avisar
    private string? _confirmedUserName;

    private async void OnSkipPasswordClicked(object sender, RoutedEventArgs e)
        => await SaveAsync(UserNameBox.Text.Trim(), null);

    private void ShowCredentialsError(string message)
    {
        CredentialsError.Text = message;
        CredentialsError.Visibility = Visibility.Visible;
    }

    // ===== Paso 3: guardar =====

    private async Task SaveAsync(string userName, string? password)
    {
        if (_busy || SelectedDevice is not { IsSupported: true } device)
        {
            return;
        }

        _busy = true;
        ShowStep(3);
        SavingRow.Visibility = Visibility.Visible;
        ResultBox.Visibility = Visibility.Collapsed;
        try
        {
            _config.ApplySetup(device.Device, userName, password);
            if (!_config.CanEdit)
            {
                ShowFailure(UiStrings.Wizard_FailedTitle, _config.PathWarningText);
                return;
            }

            var preparation = _config.PrepareSave();
            if (!preparation.Proceed)
            {
                // «Sin cambios» no es un fallo: lo elegido ya era la configuración guardada
                if (_config.BannerSeverity == InfoBarSeverity.Informational)
                {
                    ShowSuccess(device, null);
                }
                else
                {
                    ShowFailure(_config.BannerTitle, _config.BannerMessage);
                }

                return;
            }

            await _config.SaveAsync();

            // Servicio parado: en el asistente se arranca sin preguntar, para que lo configurado surta efecto
            if (_config.BannerActionKind == ConfigBannerAction.StartService)
            {
                await _config.RunBannerActionAsync(ConfigBannerAction.StartService);
                if (_config.BannerSeverity == InfoBarSeverity.Error)
                {
                    ShowFailure(UiStrings.Wizard_FailedTitle,
                        string.Format(UiStrings.Wizard_ServiceStartFailed, _config.BannerMessage));
                    return;
                }
            }

            if (_config.BannerSeverity == InfoBarSeverity.Error)
            {
                ShowFailure(_config.BannerTitle, _config.BannerMessage);
                return;
            }

            // Aviso (recarga sin confirmar) o éxito: se guardó; el detalle del servicio va debajo
            ShowSuccess(device, _config.BannerSeverity == InfoBarSeverity.Warning ? _config.BannerMessage : null);
        }
        catch (Exception ex)
        {
            ShowFailure(UiStrings.Wizard_FailedTitle, ex.Message);
        }
        finally
        {
            _busy = false;
            SavingRow.Visibility = Visibility.Collapsed;
            UpdateFooter();
        }
    }

    private void ShowSuccess(SetupWizardDevice device, string? detail)
    {
        Completed = true;
        var lockOnly = _config.SavedSetupState != SetupState.Complete;
        ShowResult(
            UiStrings.Wizard_DoneTitle,
            string.Format(lockOnly ? UiStrings.Wizard_DoneLockOnly : UiStrings.Wizard_DoneFull, device.Device.DisplayName),
            detail,
            lockOnly ? "warning" : "success");
    }

    private void ShowFailure(string title, string message) => ShowResult(title, message, null, "danger");

    private void ShowResult(string title, string message, string? detail, string tone)
    {
        ResultTitle.Text = title;
        ResultMessage.Text = message;
        ResultDetail.Text = detail ?? "";
        ResultDetail.Visibility = Show(!string.IsNullOrEmpty(detail));

        var (background, foreground, symbol) = tone switch
        {
            "success" => ("Ui.Status.Success.Bg", "Ui.Status.Success.Fg", SymbolRegular.CheckmarkCircle20),
            "warning" => ("Ui.Status.Warning.Bg", "Ui.Status.Warning.Fg", SymbolRegular.CheckmarkCircle20),
            _ => ("Ui.Status.Danger.Bg", "Ui.Status.Danger.Fg", SymbolRegular.ErrorCircle20),
        };
        ResultBox.SetResourceReference(Border.BackgroundProperty, background);
        ResultIcon.SetResourceReference(SymbolIcon.ForegroundProperty, foreground);
        ResultIcon.Symbol = symbol;
        ResultBox.Visibility = Visibility.Visible;
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}

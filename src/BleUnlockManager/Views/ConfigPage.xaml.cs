using System.Diagnostics;
using System.ComponentModel;
using System.Windows.Controls;
using BleUnlockManager.Core;
using BleUnlockManager.Core.Config;
using BleUnlockManager.ViewModels;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;
using TextBlock = System.Windows.Controls.TextBlock;
using BleUnlockManager.Core.Resources;

namespace BleUnlockManager.Views;

public partial class ConfigPage : UserControl
{
    private readonly IContentDialogService _dialogs;
    private bool _loadedOnce;

    public ConfigPage(ConfigViewModel viewModel, IContentDialogService dialogs)
    {
        ViewModel = viewModel;
        _dialogs = dialogs;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    public ConfigViewModel ViewModel { get; }

    // Cada vez que se entra en la página: primera carga, o relectura si no hay ediciones pendientes
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_loadedOnce)
        {
            _loadedOnce = true;
            ViewModel.Initialize();
        }
        else if (!ViewModel.IsDirty || !ViewModel.CanEdit)
        {
            ViewModel.ReloadFromDisk();
        }
    }

    // —— Contraseña: la guardada nunca entra en el campo ——

    // El enlace del PasswordBox de WPF UI no propaga del modelo al control: al vaciar el campo
    // (cargar, guardar, descartar) el control conservaría lo escrito y lo siguiente se le añadiría.
    // Se sincroniza a mano: primero se vuelve a la máscara, después se escribe el valor del modelo
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConfigViewModel.PasswordText) || PasswordInput.Password == ViewModel.PasswordText)
        {
            return;
        }

        if (!ViewModel.CanRevealPassword)
        {
            ConcealPassword();
        }

        PasswordInput.Password = ViewModel.PasswordText;
        if (ViewModel.PasswordText.Length == 0)
        {
            // Sin esto el control mantiene la máscara anterior («**») con la contraseña ya vacía
            PasswordInput.Text = string.Empty;
        }
    }

    // WPF UI solo expone IsPasswordRevealed como lectura (setter privado, lo cambia el botón del ojo).
    // Si el nombre cambiara en otra versión, no hace nada: el ojo ya está oculto en ese caso
    private void ConcealPassword()
    {
        if (!PasswordInput.IsPasswordRevealed)
        {
            return;
        }

        typeof(Wpf.Ui.Controls.PasswordBox)
            .GetProperty(nameof(Wpf.Ui.Controls.PasswordBox.IsPasswordRevealed))?
            .GetSetMethod(nonPublic: true)?
            .Invoke(PasswordInput, [false]);
    }

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var preparation = ViewModel.PrepareSave();
            if (!preparation.Proceed || !await ConfirmChangesAsync(preparation.Changes))
            {
                return;
            }

            await ViewModel.SaveAsync();
        }
        catch (Exception ex)
        {
            ViewModel.ShowError(UiStrings.Save_FailedTitle, ex.Message);
        }
    }

    private void OnDiscardClicked(object sender, RoutedEventArgs e) => ViewModel.DiscardChanges();

    private async void OnReloadClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ViewModel.IsDirty &&
                !await ConfirmAsync(UiStrings.Reload_DiscardTitle, UiStrings.Reload_DiscardMessage, UiStrings.Reload_Button))
            {
                return;
            }

            ViewModel.ReloadFromDisk();
        }
        catch (Exception ex)
        {
            ViewModel.ShowError(UiStrings.Reload_FailedTitle, ex.Message);
        }
    }

    private void OnOpenFolderClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = AppPaths.DataDir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ViewModel.ShowError(UiStrings.OpenFolder_FailedTitle, ex.Message);
        }
    }

    private async void OnBannerActionClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var action = ViewModel.BannerActionKind;
            if (action == ConfigBannerAction.None)
            {
                return;
            }

            var (title, content, primary) = action == ConfigBannerAction.StartService
                ? (UiStrings.Common_StartService, UiStrings.Confirm_StartService, UiStrings.Common_StartService)
                : (UiStrings.Common_RestartService, UiStrings.Confirm_RestartInterrupts, UiStrings.Common_RestartService);
            if (await ConfirmAsync(title, content, primary))
            {
                await ViewModel.RunBannerActionAsync(action);
            }
        }
        catch (Exception ex)
        {
            ViewModel.ShowError(UiStrings.Common_OperationFailed, ex.Message);
        }
    }

    private async void OnRefreshBondsClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.LoadBondedDevicesAsync();
        }
        catch (Exception ex)
        {
            ViewModel.ShowError(UiStrings.Bonds_ListFailedTitle, ex.Message);
        }
    }

    private async void OnPairDeviceClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Window.GetWindow(this) is not MainWindow window)
            {
                return;
            }

            var dialog = new PairDeviceDialog();
            await window.ShowOverlayAsync(dialog, dialog.Completion);
            if (dialog.PairedDevice is not { } device)
            {
                return;
            }

            if (await ViewModel.SelectBondedDeviceAsync(device.Address))
            {
                ViewModel.ShowBanner(InfoBarSeverity.Success, UiStrings.Pair_SucceededTitle, string.Format(UiStrings.Pair_SelectedSave, device.DisplayName));
            }
        }
        catch (Exception ex)
        {
            ViewModel.ShowError(UiStrings.Pair_FailedTitle, ex.Message);
        }
    }

    private async void OnUnpairDeviceClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var device = ViewModel.SelectedBondDevice;
            if (device is null || device.Address.Length == 0)
            {
                return;
            }

            var content = ViewModel.IsSelectedDeviceActive
                ? string.Format(UiStrings.Unpair_ConfirmActive, device.DisplayName) +
                  (ViewModel.IsDirty ? UiStrings.Unpair_ConfirmDirtySuffix : "")
                : string.Format(UiStrings.Unpair_ConfirmInactive, device.DisplayName);
            if (await ConfirmAsync(UiStrings.Config_Unpair, content, UiStrings.Config_Unpair))
            {
                await ViewModel.UnpairSelectedDeviceAsync();
            }
        }
        catch (Exception ex)
        {
            ViewModel.ShowError(UiStrings.Unpair_FailedTitle, ex.Message);
        }
    }

    private async Task<bool> ConfirmChangesAsync(IReadOnlyList<ConfigChange> changes)
    {
        var panel = new StackPanel();
        foreach (var change in changes)
        {
            panel.Children.Add(new TextBlock
            {
                Text = change.IsSensitive || change.Field == ConfigFields.DeviceIrk
                    ? string.Format(UiStrings.Diff_Modified, change.DisplayName)
                    : string.Format(UiStrings.Diff_Change, change.DisplayName, change.OldText, change.NewText),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });
        }

        var result = await _dialogs.ShowSimpleDialogAsync(new SimpleContentDialogCreateOptions
        {
            Title = UiStrings.Save_ConfirmTitle,
            Content = new ScrollViewer { Content = panel, MaxHeight = 360 },
            PrimaryButtonText = UiStrings.Config_Save,
            CloseButtonText = UiStrings.Common_Cancel,
        });
        return result == ContentDialogResult.Primary;
    }

    private async Task<bool> ConfirmAsync(string title, string content, string primary)
    {
        var result = await _dialogs.ShowSimpleDialogAsync(new SimpleContentDialogCreateOptions
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primary,
            CloseButtonText = UiStrings.Common_Cancel,
        });
        return result == ContentDialogResult.Primary;
    }
}

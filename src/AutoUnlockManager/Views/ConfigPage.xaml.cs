using System.Diagnostics;
using System.ComponentModel;
using System.Windows.Controls;
using AutoUnlockManager.Core;
using AutoUnlockManager.Core.Config;
using AutoUnlockManager.ViewModels;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;
using TextBlock = System.Windows.Controls.TextBlock;

namespace AutoUnlockManager.Views;

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
            ViewModel.ShowError("保存失败", ex.Message);
        }
    }

    private void OnDiscardClicked(object sender, RoutedEventArgs e) => ViewModel.DiscardChanges();

    private async void OnReloadClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ViewModel.IsDirty &&
                !await ConfirmAsync("放弃未保存的更改？", "将从磁盘重新加载配置，当前表单中的未保存修改将丢失。", "重新加载"))
            {
                return;
            }

            ViewModel.ReloadFromDisk();
        }
        catch (Exception ex)
        {
            ViewModel.ShowError("重新加载失败", ex.Message);
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
            ViewModel.ShowError("打开文件夹失败", ex.Message);
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
                ? ("启动服务", "要启动 AutoUnlockService 服务吗？", "启动服务")
                : ("重启服务", "重启将短暂中断自动锁定与解锁。要重启 AutoUnlockService 服务吗？", "重启服务");
            if (await ConfirmAsync(title, content, primary))
            {
                await ViewModel.RunBannerActionAsync(action);
            }
        }
        catch (Exception ex)
        {
            ViewModel.ShowError("操作失败", ex.Message);
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
            ViewModel.ShowError("读取设备列表失败", ex.Message);
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
                ViewModel.ShowBanner(InfoBarSeverity.Success, "配对成功", $"已选择「{device.DisplayName}」；请保存更改使其生效。");
            }
        }
        catch (Exception ex)
        {
            ViewModel.ShowError("配对失败", ex.Message);
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
                ? $"解除与「{device.DisplayName}」的 Windows 蓝牙配对？\n\n" +
                  "该设备正是当前配置使用的设备：将同时清空 IRK 与设备地址，自动解锁暂停（服务继续运行以读取配对库）。" +
                  (ViewModel.IsDirty ? "\n\n当前未保存的其他修改也会丢失。" : "")
                : $"解除与「{device.DisplayName}」的 Windows 蓝牙配对？\n\n当前服务配置不受影响。";
            if (await ConfirmAsync("解除配对", content, "解除配对"))
            {
                await ViewModel.UnpairSelectedDeviceAsync();
            }
        }
        catch (Exception ex)
        {
            ViewModel.ShowError("解除配对失败", ex.Message);
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
                    ? $"{change.DisplayName}：已修改"
                    : $"{change.DisplayName}：{change.OldText} → {change.NewText}",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });
        }

        var result = await _dialogs.ShowSimpleDialogAsync(new SimpleContentDialogCreateOptions
        {
            Title = "确认保存更改",
            Content = new ScrollViewer { Content = panel, MaxHeight = 360 },
            PrimaryButtonText = "保存更改",
            CloseButtonText = "取消",
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
            CloseButtonText = "取消",
        });
        return result == ContentDialogResult.Primary;
    }
}

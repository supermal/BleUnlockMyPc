using System.Diagnostics;
using AutoUnlockManager.Core;
using AutoUnlockManager.Core.Config;
using AutoUnlockManager.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AutoUnlockManager.Views;

/// <summary>
/// 配置页（Task 5）：全字段表单（4 组 15 字段）+ 校验（镜像服务端规则）+ 差异确认对话框 +
/// 原子保存与热重载确认 + 服务启停辅助。敏感字段以 dpapi 密文存储；IRK 不再手工输入，
/// 从「已配对 BLE 设备」下拉框选择（服务读取 Windows 配对库），密码仍以掩码+眼睛回显。
/// ViewModel 全程 UI 线程、无后台任务，无需 Dispose。
/// </summary>
public sealed partial class ConfigPage : Page
{
    private bool _loadedOnce;
    private bool _loadPending;

    public ConfigPage()
    {
        ViewModel = new ConfigViewModel();
        InitializeComponent();
    }

    /// <summary>视图模型（x:Bind 数据源）。</summary>
    public ConfigViewModel ViewModel { get; }

    /// <summary>
    /// 页面缓存后，首次进入仍要等导航完成再读配置，避免在导航动画期间同步做
    /// 文件读取/DPAPI 解密/绑定刷新；再次进入时刷新磁盘值，但有未保存修改时保留表单。
    /// </summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (_loadPending)
        {
            return;
        }

        _loadPending = true;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, LoadForNavigation))
        {
            _loadPending = false;
            LoadForNavigation();
        }
    }

    private void LoadForNavigation()
    {
        _loadPending = false;
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

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var preparation = ViewModel.PrepareSave();
            if (!preparation.Proceed)
            {
                return; // 校验失败 / 无更改：横幅已由 VM 显示
            }

            if (!await ConfirmChangesAsync(preparation.Changes))
            {
                return;
            }

            await ViewModel.SaveAsync();
            RevealToggle.IsChecked = false;    // 保存后回到掩码显示（安全卫生，评审 M5）
        }
        catch (Exception ex)
        {
            ViewModel.ShowError("保存失败", ex.Message); // async void 兜底，异常不得冒泡
        }
    }

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
            // 用 UseShellExecute 打开资源管理器（路径存在性由按钮启用状态约束）
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
                ? ("启动服务", "要启动 AutoUnlockService 服务吗？", "启动")
                : ("重启服务", "重启将短暂中断自动锁定/解锁。要重启 AutoUnlockService 服务吗？", "重启");

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

    private void OnRevealChanged(object sender, RoutedEventArgs e)
        => PasswordInput.PasswordRevealMode = RevealToggle.IsChecked == true
            ? PasswordRevealMode.Visible
            : PasswordRevealMode.Hidden;

    private async void OnRefreshBondsClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.LoadBondedDevicesAsync();
        }
        catch (Exception ex)
        {
            ViewModel.ShowError("读取设备列表失败", ex.Message); // async void 兜底
        }
    }

    private async void OnPairDeviceClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new PairDeviceDialog { XamlRoot = XamlRoot };
            await dialog.ShowAsync();
            if (dialog.PairedDevice is not { } device)
            {
                return;
            }

            if (await ViewModel.SelectBondedDeviceAsync(device.Address))
            {
                ViewModel.ShowBanner(InfoBarSeverity.Success, "配对成功",
                    $"已选择「{device.DisplayName}」；请保存配置使其生效。");
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

            var active = ViewModel.IsSelectedDeviceActive;
            var content = active
                ? $"解除与「{device.DisplayName}」的 Windows 蓝牙配对？\n\n" +
                  "该设备正是磁盘配置当前使用的设备：将同时清空 IRK/设备地址，自动解锁暂停（服务继续运行以读取配对库）。" +
                  (ViewModel.IsDirty ? "\n\n当前未保存的其他修改也会丢失。" : "")
                : $"解除与「{device.DisplayName}」的 Windows 蓝牙配对？\n\n当前服务配置不受影响。";

            if (!await ConfirmAsync("解除配对", content, "解除配对"))
            {
                return;
            }

            await ViewModel.UnpairSelectedDeviceAsync();
        }
        catch (Exception ex)
        {
            ViewModel.ShowError("解除配对失败", ex.Message);
        }
    }

    /// <summary>差异摘要确认对话框（凭据密码仅显示「已修改」）。</summary>
    private async Task<bool> ConfirmChangesAsync(IReadOnlyList<ConfigChange> changes)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var change in changes)
        {
            var text = change.IsSensitive
                ? $"{change.DisplayName}：已修改"
                : $"{change.DisplayName}：{change.OldText} → {change.NewText}";
            panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        }

        var dialog = new ContentDialog
        {
            Title = "确认保存更改",
            Content = new ScrollViewer { Content = panel, MaxHeight = 360 },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch
        {
            return false; // 极端场景（已有对话框展示/XamlRoot 失效）：忽略本次触发
        }
    }

    private async Task<bool> ConfirmAsync(string title, string content, string primary)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch
        {
            return false;
        }
    }
}

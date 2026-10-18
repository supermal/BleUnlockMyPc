using System.Diagnostics;
using AutoUnlockManager.Core;
using AutoUnlockManager.Core.Config;
using AutoUnlockManager.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AutoUnlockManager.Views;

/// <summary>
/// 配置页（Task 5）：全字段表单（4 组 15 字段）+ 校验（镜像服务端规则）+ 差异确认对话框 +
/// 原子保存与热重载确认 + 服务启停辅助。敏感字段（IRK/密码）以 dpapi 密文存储，界面不回显（留空保持不变）。
/// ViewModel 全程 UI 线程、无后台任务，无需 Dispose。
/// </summary>
public sealed partial class ConfigPage : Page
{
    public ConfigPage()
    {
        ViewModel = new ConfigViewModel();
        InitializeComponent();
        ViewModel.Initialize();
    }

    /// <summary>视图模型（x:Bind 数据源）。</summary>
    public ConfigViewModel ViewModel { get; }

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
            IrkRevealToggle.IsChecked = false; // IRK 同规则（用户要求）
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

    private void OnIrkRevealChanged(object sender, RoutedEventArgs e)
        => IrkInput.PasswordRevealMode = IrkRevealToggle.IsChecked == true
            ? PasswordRevealMode.Visible
            : PasswordRevealMode.Hidden;

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
            Content = content,
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

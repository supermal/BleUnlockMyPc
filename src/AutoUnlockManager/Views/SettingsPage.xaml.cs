using AutoUnlockManager.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AutoUnlockManager.Views;

/// <summary>
/// 设置页：外观（主题）选择——跟随系统/浅色/深色。
/// 选择经 <see cref="ThemeService"/> 即时应用并持久化（%LocalAppData%\AutoUnlockManager\ui-settings.json）。
/// </summary>
public sealed partial class SettingsPage : Page
{
    /// <summary>初始化选择期间抑制 SelectionChanged 回写（防初始选中触发保存）。</summary>
    private bool _suppressSelection;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _suppressSelection = true;
        ThemeRadio.SelectedIndex = ThemeService.Current switch
        {
            ElementTheme.Light => 1,
            ElementTheme.Dark => 2,
            _ => 0,
        };
        _suppressSelection = false;
    }

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection)
        {
            return;
        }

        var theme = ThemeRadio.SelectedIndex switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        ThemeService.SetTheme(theme);
    }
}

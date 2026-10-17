using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace AutoUnlockManager.Services;

/// <summary>
/// 应用外观服务：主题（跟随系统/浅色/深色）在窗口根元素上即时生效，
/// 选择持久化到 %LocalAppData%\AutoUnlockManager\ui-settings.json，
/// 并同步自定义标题栏的系统按钮配色（设计 §5 外观约定）。
/// </summary>
internal static class ThemeService
{
    private static MainWindow? _window;
    private static FrameworkElement? _root;
    private static ElementTheme _current = ElementTheme.Default;

    /// <summary>当前选择的主题（<see cref="ElementTheme.Default"/> = 跟随系统）。</summary>
    public static ElementTheme Current => _current;

    /// <summary>
    /// 启动时调用：读取已保存主题 → 应用到窗口根元素（首帧前，无闪烁）→
    /// 挂接实际主题变化（跟随系统时系统切换联动）→ 同步标题栏按钮配色。
    /// </summary>
    public static void Initialize(MainWindow window)
    {
        _window = window;
        _root = window.Content as FrameworkElement;
        _current = Load();
        Apply(_current, persist: false);

        if (_root is not null)
        {
            _root.ActualThemeChanged += (_, _) => UpdateTitleBarButtons();
            UpdateTitleBarButtons();
        }
    }

    /// <summary>切换主题（设置页调用）：立即应用并持久化。</summary>
    public static void SetTheme(ElementTheme theme)
    {
        _current = theme;
        Apply(theme, persist: true);
    }

    private static void Apply(ElementTheme theme, bool persist)
    {
        if (_root is not null)
        {
            _root.RequestedTheme = theme;
        }

        UpdateTitleBarButtons();

        if (persist)
        {
            Save(theme);
        }
    }

    /// <summary>标题栏系统按钮配色随实际主题（仅支持定制的系统；失败仅影响观感）。</summary>
    private static void UpdateTitleBarButtons()
    {
        if (_window is null || !AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        try
        {
            var dark = _root?.ActualTheme == ElementTheme.Dark;
            var titleBar = _window.AppWindow.TitleBar;
            var foreground = dark ? Colors.White : Colors.Black;

            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = foreground;
            titleBar.ButtonHoverForegroundColor = foreground;
            titleBar.ButtonPressedForegroundColor = foreground;
            titleBar.ButtonInactiveForegroundColor = dark
                ? Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x66, 0x00, 0x00, 0x00);
            titleBar.ButtonHoverBackgroundColor = dark
                ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x1A, 0x00, 0x00, 0x00);
            titleBar.ButtonPressedBackgroundColor = dark
                ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x33, 0x00, 0x00, 0x00);
        }
        catch
        {
            // 标题栏配色失败仅影响观感
        }
    }

    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoUnlockManager");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "ui-settings.json");

    private sealed class UiSettings
    {
        public string? Theme { get; set; }
    }

    private static ElementTheme Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(SettingsPath));
                return settings?.Theme switch
                {
                    "Light" => ElementTheme.Light,
                    "Dark" => ElementTheme.Dark,
                    _ => ElementTheme.Default,
                };
            }
        }
        catch
        {
            // 读取失败按跟随系统处理
        }

        return ElementTheme.Default;
    }

    private static void Save(ElementTheme theme)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var name = theme switch
            {
                ElementTheme.Light => "Light",
                ElementTheme.Dark => "Dark",
                _ => "System",
            };
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new UiSettings { Theme = name }));
        }
        catch
        {
            // 保存失败不影响本次切换
        }
    }
}

using System.Windows.Media;
using BleUnlockManager.Models;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace BleUnlockManager.Helpers;

/// <summary>
/// Sustituye el diccionario de pinceles (Light / Dark / alto contraste) según el tema de WPF UI.
/// </summary>
public static class UiTheme
{
    // Clave presente en cada diccionario de tema para poder localizarlo
    private const string MarkerKey = "Ui.Theme.Name";

    private static bool _isSubscribed;

    /// <summary>
    /// Aplica el tema actual y se suscribe a los cambios de tema de WPF UI.
    /// </summary>
    public static void Initialize()
    {
        Apply(ApplicationThemeManager.GetAppTheme());

        if (!_isSubscribed)
        {
            ApplicationThemeManager.Changed += (theme, _) => Apply(theme);
            _isSubscribed = true;
        }
    }

    /// <summary>
    /// Aplica la preferencia del usuario. "Sistema" vuelve a observar los cambios de Windows;
    /// claro y oscuro dejan de observarlos para que la elección no se sobrescriba.
    /// </summary>
    public static void SetMode(AppThemeMode mode)
    {
        var window = Application.Current.MainWindow;

        if (window is not null)
        {
            // Se deja de observar primero para no registrar el mismo gancho dos veces
            SystemThemeWatcher.UnWatch(window);
        }

        if (mode == AppThemeMode.System)
        {
            if (window is not null)
            {
                SystemThemeWatcher.Watch(window, WindowBackdropType.None, true);
            }

            ApplicationThemeManager.Apply(GetSystemTheme(), WindowBackdropType.None, true);
            return;
        }

        var theme = mode == AppThemeMode.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        ApplicationThemeManager.Apply(theme, WindowBackdropType.None, true);
    }

    /// <summary>
    /// Tema actual de Windows traducido a un tema de la aplicación.
    /// </summary>
    private static ApplicationTheme GetSystemTheme()
    {
        SystemThemeManager.UpdateSystemThemeCache();

        if (ApplicationThemeManager.IsSystemHighContrast())
        {
            return ApplicationTheme.HighContrast;
        }

        return ApplicationThemeManager.GetSystemTheme() switch
        {
            SystemTheme.Dark or SystemTheme.CapturedMotion or SystemTheme.Glow => ApplicationTheme.Dark,
            _ => ApplicationTheme.Light,
        };
    }

    public static void Apply(ApplicationTheme theme)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var current = dictionaries.FirstOrDefault(d => d.Contains(MarkerKey));

        var next = theme switch
        {
            ApplicationTheme.Dark => Load("Dark"),
            ApplicationTheme.HighContrast => BuildHighContrast(),
            _ => Load("Light"),
        };

        if (current is null)
        {
            dictionaries.Add(next);
            return;
        }

        // Se conserva la posición original para no alterar la precedencia de recursos
        var index = dictionaries.IndexOf(current);
        dictionaries[index] = next;
    }

    private static ResourceDictionary Load(string name) => new()
    {
        Source = new Uri($"pack://application:,,,/Themes/{name}.xaml", UriKind.Absolute),
    };

    /// <summary>
    /// En alto contraste se usan los colores del sistema (UiDesign.md, sección 6).
    /// </summary>
    private static ResourceDictionary BuildHighContrast()
    {
        var dictionary = Load("Light");
        var keys = dictionary.Keys.OfType<string>().ToList();

        foreach (var key in keys)
        {
            if (dictionary[key] is not SolidColorBrush)
            {
                continue;
            }

            dictionary[key] = MapToSystemBrush(key);
        }

        dictionary[MarkerKey] = "HighContrast";
        return dictionary;
    }

    private static Brush MapToSystemBrush(string key) => key switch
    {
        "Ui.Bg.Overlay" => new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)),
        "Ui.Bg.Subtle.Hover" or "Ui.Bg.Subtle.Selected" => Brushes.Transparent,
        "Ui.Bg.Control" or "Ui.Bg.Control.Hover" or "Ui.Bg.Control.Pressed" => SystemColors.ControlBrush,
        _ when key.StartsWith("Ui.Bg.", StringComparison.Ordinal) => SystemColors.WindowBrush,
        _ when key.StartsWith("Ui.Stroke.", StringComparison.Ordinal) => SystemColors.WindowTextBrush,
        "Ui.Text.Tertiary" or "Ui.Text.Disabled" => SystemColors.GrayTextBrush,
        "Ui.Text.Link" => SystemColors.HotTrackBrush,
        "Ui.OnPrimary" => SystemColors.HighlightTextBrush,
        "Ui.Primary" or "Ui.Primary.Hover" or "Ui.Primary.Pressed" or "Ui.Indicator" => SystemColors.HighlightBrush,
        _ when key.EndsWith(".Bg", StringComparison.Ordinal) => SystemColors.WindowBrush,
        _ => SystemColors.WindowTextBrush,
    };
}

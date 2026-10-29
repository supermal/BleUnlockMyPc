using System.Diagnostics;
using AutoUnlockManager.Core;
using AutoUnlockManager.Helpers;
using AutoUnlockManager.Models;

namespace AutoUnlockManager.ViewModels;

/// <summary>Ajustes del propio gestor: tema, arranque con la sesión y ubicación de los ficheros.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ManagerSettings _settings = ManagerSettings.Load(AppPaths.ManagerSettingsFile);

    public SettingsViewModel()
    {
        AutoStartText = "正在检查…";
        AutoStartTone = "neutral";
    }

    public ThemePreference Theme => _settings.Theme;

    public bool IsSystemTheme
    {
        get => Theme == ThemePreference.System;
        set { if (value) SetTheme(ThemePreference.System); }
    }

    public bool IsLightTheme
    {
        get => Theme == ThemePreference.Light;
        set { if (value) SetTheme(ThemePreference.Light); }
    }

    public bool IsDarkTheme
    {
        get => Theme == ThemePreference.Dark;
        set { if (value) SetTheme(ThemePreference.Dark); }
    }

    [ObservableProperty] public partial string AutoStartText { get; set; }

    /// <summary>ok | warn | neutral</summary>
    [ObservableProperty] public partial string AutoStartTone { get; set; }

    public string ConfigFilePath => AppPaths.ConfigFile;

    public string LogsDirPath => AppPaths.LogsDir;

    /// <summary>El gestor se instala en &lt;InstallDir&gt;\Manager: el directorio de instalación es el padre.</summary>
    public string InstallDirPath =>
        Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)) ?? AppContext.BaseDirectory;

    /// <summary>Aplica el tema guardado (al arrancar la aplicación).</summary>
    public void ApplySavedTheme() => UiTheme.SetMode(ToMode(Theme));

    public async Task RefreshAutoStartAsync()
    {
        var registered = await Task.Run(IsAutoStartRegistered);
        (AutoStartText, AutoStartTone) = registered switch
        {
            true => ("已启用", "ok"),
            false => ("未注册（请运行安装脚本）", "warn"),
            null => ("无法确认", "neutral"),
        };
    }

    private void SetTheme(ThemePreference theme)
    {
        if (theme == _settings.Theme)
        {
            return;
        }

        _settings.Theme = theme;
        UiTheme.SetMode(ToMode(theme));
        try
        {
            _settings.Save(AppPaths.ManagerSettingsFile);
        }
        catch
        {
            // Sin poder guardar, el tema se aplica igualmente en esta sesión
        }

        OnPropertyChanged(nameof(IsSystemTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsDarkTheme));
    }

    private static AppThemeMode ToMode(ThemePreference theme) => theme switch
    {
        ThemePreference.Light => AppThemeMode.Light,
        ThemePreference.Dark => AppThemeMode.Dark,
        _ => AppThemeMode.System,
    };

    // La tarea programada la registra install.ps1; código de salida 0 = existe
    private static bool? IsAutoStartRegistered()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = "/Query /TN AutoUnlockManager",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null || !process.WaitForExit(5000))
            {
                return null;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return null;
        }
    }
}

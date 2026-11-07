using System.Diagnostics;
using System.Globalization;
using BleUnlock.Shared;
using BleUnlockManager.Core;
using BleUnlockManager.Core.Config;
using BleUnlockManager.Core.Localization;
using BleUnlockManager.Core.Resources;
using BleUnlockManager.Helpers;
using BleUnlockManager.Models;

namespace BleUnlockManager.ViewModels;

/// <summary>Ajustes del propio gestor: tema, arranque con la sesión y ubicación de los ficheros.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ManagerSettings _settings = ManagerSettings.Load(AppPaths.ManagerSettingsFile);

    // Orden de las opciones del desplegable de idioma
    private static readonly string[] LanguageCodes = [AppLanguages.SystemSetting, "zh", "en", "es"];

    private string _languageCode;
    private string? _languageError;

    public SettingsViewModel()
    {
        AutoStartText = UiStrings.Settings_Checking;
        AutoStartTone = "neutral";

        var saved = ConfigDocument.ReadLanguage(AppPaths.ConfigFile);
        _languageCode = AppLanguages.TryParseCode(saved, out var language)
            ? AppLanguages.ToCode(language)
            : AppLanguages.SystemSetting;

        // Los textos calculados aquí también cambian de idioma
        ManagerLanguage.Changed += () =>
        {
            OnPropertyChanged(nameof(Version));
            OnPropertyChanged(nameof(LanguageNote));
            OnPropertyChanged(nameof(HasLanguageNote));
            _ = RefreshAutoStartAsync();
        };
    }

    // —— Acerca de ——

    /// <summary>Versión del paquete instalado (la misma del nombre del instalador).</summary>
    public string Version => AppVersion.Display(typeof(SettingsViewModel).Assembly);

    public string RepositoryUrl => "https://github.com/supermal/BleUnlockMyPc";

    public string IssuesUrl => "https://github.com/supermal/BleUnlockMyPc/issues";

    public string CloneCommand => $"git clone {RepositoryUrl}.git";

    public string Copyright => "© 2026 supermal | BleUnlock";

    // —— Idioma (se guarda en la configuración del servicio: uno por equipo) ——

    /// <summary>Sin configuración del servicio no hay dónde guardar el idioma.</summary>
    public bool CanChangeLanguage => File.Exists(AppPaths.ConfigFile);

    public int LanguageIndex
    {
        get => Math.Max(0, Array.IndexOf(LanguageCodes, _languageCode));
        set
        {
            if (value >= 0 && value < LanguageCodes.Length)
            {
                SetLanguage(LanguageCodes[value]);
            }
        }
    }

    public string LanguageNote => !CanChangeLanguage
        ? UiStrings.Settings_LanguageNeedsService
        : _languageError ?? "";

    public bool HasLanguageNote => LanguageNote.Length > 0;

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
            true => (UiStrings.Settings_AutoStartEnabled, "ok"),
            false => (UiStrings.Settings_AutoStartMissing, "warn"),
            null => (UiStrings.Settings_AutoStartUnknown, "neutral"),
        };
    }

    private void SetLanguage(string code)
    {
        if (code == _languageCode || !CanChangeLanguage)
        {
            return;
        }

        try
        {
            ConfigDocument.SaveLanguage(AppPaths.ConfigFile, code);
            _languageCode = code;
            _languageError = null;
            ManagerLanguage.Set(AppLanguages.Resolve(code, CultureInfo.CurrentUICulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // Sin guardar no se cambia: el desplegable vuelve a la opción anterior
            _languageError = string.Format(UiStrings.Settings_LanguageSaveFailed, ex.Message);
        }

        OnPropertyChanged(nameof(LanguageIndex));
        OnPropertyChanged(nameof(LanguageNote));
        OnPropertyChanged(nameof(HasLanguageNote));
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
                Arguments = "/Query /TN BleUnlockManager",
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

using System.Globalization;

namespace BleUnlock.Shared;

// Idiomas de la interfaz. Fichero enlazado en el servicio, el asistente y el núcleo del gestor:
// las tres partes deciden el idioma con la misma regla
public enum AppLanguage { Chinese, English, Spanish }

public static class AppLanguages
{
    // Valor de configuración que delega en el idioma del sistema
    public const string SystemSetting = "system";

    public static bool TryParseCode(string? code, out AppLanguage language)
    {
        switch (code?.Trim().ToLowerInvariant())
        {
            case "zh": language = AppLanguage.Chinese; return true;
            case "en": language = AppLanguage.English; return true;
            case "es": language = AppLanguage.Spanish; return true;
            default: language = AppLanguage.English; return false;
        }
    }

    public static string ToCode(AppLanguage language) => language switch
    {
        AppLanguage.Chinese => "zh",
        AppLanguage.Spanish => "es",
        _ => "en",
    };

    // Chino tradicional incluido: se muestra en simplificado. Cualquier otro idioma cae en inglés
    public static AppLanguage FromCulture(CultureInfo culture) => culture.TwoLetterISOLanguageName switch
    {
        "zh" => AppLanguage.Chinese,
        "es" => AppLanguage.Spanish,
        _ => AppLanguage.English,
    };

    // Un código válido manda; «system», vacío o desconocido sigue a la cultura del sistema
    public static AppLanguage Resolve(string? setting, CultureInfo systemCulture) =>
        TryParseCode(setting, out var language) ? language : FromCulture(systemCulture);

    public static CultureInfo ToCulture(AppLanguage language) =>
        CultureInfo.GetCultureInfo(ToCode(language));
}

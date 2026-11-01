using BleUnlock.Shared;
using BleUnlockManager.Core.Resources;

namespace BleUnlockManager.Core.Localization;

/// <summary>
/// Idioma de la interfaz del gestor para todo el proceso. Fija el de las clases de recursos (núcleo e
/// interfaz) y después avisa, para que quien escuche <see cref="Changed"/> ya lea los textos nuevos (cambio inmediato, sin reiniciar).
/// </summary>
public static class ManagerLanguage
{
    private static readonly object Gate = new();

    public static AppLanguage Current { get; private set; } = AppLanguage.English;

    /// <summary>Solo cuando el idioma cambia de verdad.</summary>
    public static event Action? Changed;

    public static void Set(AppLanguage language)
    {
        lock (Gate)
        {
            var culture = AppLanguages.ToCulture(language);
            CoreStrings.Culture = culture;
            UiStrings.Culture = culture;

            if (language == Current)
            {
                return;
            }

            Current = language;
        }

        Changed?.Invoke();
    }
}

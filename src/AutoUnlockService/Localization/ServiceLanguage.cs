using System.Globalization;
using AutoUnlock.Shared;
using AutoUnlockService.Config;
using AutoUnlockService.Resources;

namespace AutoUnlockService.Localization;

// Idioma efectivo del servicio: el ajuste de la configuración y, en modo «system», el idioma del
// usuario que informa el asistente (el servicio corre como SYSTEM y su propio idioma es el del
// equipo, que puede no coincidir con el del usuario). Fija el idioma de ServiceStrings para todo el proceso
public sealed class ServiceLanguage
{
    private readonly object _lock = new();
    private readonly OptionsGate _options;
    private readonly CultureInfo _machineCulture;
    private CultureInfo? _userCulture;

    public ServiceLanguage(OptionsGate options, CultureInfo machineCulture)
    {
        _options = options;
        _machineCulture = machineCulture;
        Current = Compute();
        ServiceStrings.Culture = AppLanguages.ToCulture(Current);
        options.Changed += _ => Recompute();
    }

    public AppLanguage Current { get; private set; }

    // Solo cuando el idioma efectivo cambia de verdad
    public event Action<AppLanguage>? Changed;

    public void SetUserCulture(string? cultureName)
    {
        CultureInfo? culture = null;
        if (!string.IsNullOrWhiteSpace(cultureName))
        {
            try { culture = CultureInfo.GetCultureInfo(cultureName.Trim()); }
            catch (CultureNotFoundException) { /* se ignora: sigue valiendo el idioma del equipo */ }
        }

        lock (_lock) _userCulture = culture;
        Recompute();
    }

    private AppLanguage Compute()
    {
        lock (_lock)
            return AppLanguages.Resolve(_options.Current.Ui?.Language, _userCulture ?? _machineCulture);
    }

    private void Recompute()
    {
        var next = Compute();
        lock (_lock)
        {
            if (next == Current) return;
            Current = next;
        }

        ServiceStrings.Culture = AppLanguages.ToCulture(next);
        Changed?.Invoke(next);
    }
}

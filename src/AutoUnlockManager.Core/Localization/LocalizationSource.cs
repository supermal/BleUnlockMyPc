using System.ComponentModel;
using System.Resources;
using AutoUnlock.Shared;

namespace AutoUnlockManager.Core.Localization;

/// <summary>
/// Origen de enlace para textos de recursos: la interfaz se enlaza a <c>[Clave]</c> y, al cambiar el
/// idioma, el aviso «Item[]» hace que WPF vuelva a leer todas las claves a la vez.
/// Una clave inexistente se muestra como <c>!Clave!</c> para que salte a la vista.
/// </summary>
public sealed class LocalizationSource : INotifyPropertyChanged
{
    private readonly ResourceManager _resources;

    public LocalizationSource(ResourceManager resources)
    {
        _resources = resources;
        ManagerLanguage.Changed += () => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] =>
        _resources.GetString(key, AppLanguages.ToCulture(ManagerLanguage.Current)) ?? $"!{key}!";
}

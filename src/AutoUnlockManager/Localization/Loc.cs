using System.Windows.Data;
using System.Windows.Markup;
using AutoUnlockManager.Core.Localization;
using AutoUnlockManager.Core.Resources;

namespace AutoUnlockManager.Localization;

/// <summary>Origen único de los textos de la interfaz para los enlaces de XAML.</summary>
public static class Loc
{
    public static LocalizationSource Ui { get; } = new(UiStrings.ResourceManager);

    /// <summary>El mismo enlace que <c>{loc:Loc Clave}</c>, para elementos creados desde código.</summary>
    public static Binding Binding(string key) => new($"[{key}]") { Source = Ui, Mode = BindingMode.OneWay };
}

/// <summary>
/// <c>{loc:Loc Clave}</c>: enlace de solo lectura al texto de la clave en el idioma actual. Al cambiar
/// el idioma, el texto se actualiza solo (sin reiniciar ni volver a cargar la página).
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        Loc.Binding(Key).ProvideValue(serviceProvider);
}

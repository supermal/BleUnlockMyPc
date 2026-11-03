using BleUnlockManager.Core.Localization;
using Wpf.Ui.Controls;

namespace BleUnlockManager.Models;

/// <summary>
/// Elemento de la navegación lateral; el contador es opcional (vacío = oculto). El título se lee de
/// los recursos cada vez, y cambia en cuanto cambia el idioma.
/// </summary>
public sealed partial class NavItem : ObservableObject
{
    private readonly Func<string> _title;

    public NavItem(AppPage page, Func<string> title, SymbolRegular icon)
    {
        Page = page;
        _title = title;
        Icon = icon;
        ManagerLanguage.Changed += () => OnPropertyChanged(nameof(Title));
    }

    public AppPage Page { get; }

    public string Title => _title();

    public SymbolRegular Icon { get; }

    [ObservableProperty]
    public partial string Count { get; set; } = "";

    /// <summary>Color del contador: «danger» (rojo) o «warning» (amarillo, por defecto).</summary>
    [ObservableProperty]
    public partial string CountTone { get; set; } = "";

    [ObservableProperty]
    public partial string CountToolTip { get; set; } = "";
}

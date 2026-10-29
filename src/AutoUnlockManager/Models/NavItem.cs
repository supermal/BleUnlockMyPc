using Wpf.Ui.Controls;

namespace AutoUnlockManager.Models;

/// <summary>Elemento de la navegación lateral; el contador es opcional (vacío = oculto).</summary>
public sealed partial class NavItem(AppPage page, string title, SymbolRegular icon) : ObservableObject
{
    public AppPage Page { get; } = page;

    public string Title { get; } = title;

    public SymbolRegular Icon { get; } = icon;

    [ObservableProperty]
    public partial string Count { get; set; } = "";
}

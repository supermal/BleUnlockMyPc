using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace BleUnlockManager.Views.Controls;

/// <summary>
/// Tarjeta de ajuste (UiDesign.md 4.10): icono opcional, título, descripción, error de validación
/// y distintivo «已修改»; el contenido es el control de la derecha. Plantilla en Themes/Controls.xaml.
/// </summary>
public sealed class SettingRow : ContentControl
{
    public static readonly DependencyProperty HeaderProperty = Register(nameof(Header), typeof(string), "");
    public static readonly DependencyProperty DescriptionProperty = Register(nameof(Description), typeof(string), "");
    public static readonly DependencyProperty ErrorProperty = Register(nameof(Error), typeof(string), "");
    public static readonly DependencyProperty IsModifiedProperty = Register(nameof(IsModified), typeof(bool), false);
    public static readonly DependencyProperty IconProperty = Register(nameof(Icon), typeof(SymbolRegular), SymbolRegular.Empty);

    public string Header { get => (string)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }

    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    public string Error { get => (string)GetValue(ErrorProperty); set => SetValue(ErrorProperty, value); }

    public bool IsModified { get => (bool)GetValue(IsModifiedProperty); set => SetValue(IsModifiedProperty, value); }

    public SymbolRegular Icon { get => (SymbolRegular)GetValue(IconProperty); set => SetValue(IconProperty, value); }

    private static DependencyProperty Register(string name, Type type, object defaultValue) =>
        DependencyProperty.Register(name, type, typeof(SettingRow), new PropertyMetadata(defaultValue));
}

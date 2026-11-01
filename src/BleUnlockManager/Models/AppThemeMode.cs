namespace BleUnlockManager.Models;

/// <summary>
/// Preferencia de tema elegida por el usuario.
/// Se llama AppThemeMode para no chocar con System.Windows.ThemeMode de .NET 9.
/// </summary>
public enum AppThemeMode
{
    Light,
    Dark,

    // Sigue el tema de Windows, incluido el alto contraste
    System,
}

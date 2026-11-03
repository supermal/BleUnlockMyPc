using System.Globalization;
using System.Reflection;
using BleUnlockManager.Core.Resources;

namespace BleUnlockManager.Core;

/// <summary>
/// Versión que se muestra en «设置 → 关于». La calcula MinVer a partir de las etiquetas git vX.Y.Z: el
/// script del instalador crea una etiqueta nueva en cada paquete, así que coincide con el nombre del
/// instalador. El SDK añade «+commit» a la versión informativa; se muestran sus 7 primeros caracteres.
/// Una compilación fuera de una etiqueta sale como preliminar (p. ej. 1.0.1-alpha.0.3) y se marca
/// como de desarrollo.
/// </summary>
public static class AppVersion
{
    private const int ShortCommitLength = 7;

    public static string Display(Assembly assembly) =>
        Display(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString());

    public static string Display(string? informationalVersion)
    {
        var parts = (informationalVersion ?? "").Trim().Split('+', 2);
        var version = parts[0].Trim();
        if (version.Length == 0)
        {
            return "—";
        }

        var commit = parts.Length > 1 ? ShortCommit(parts[1]) : null;
        bool development = version.Contains('-', StringComparison.Ordinal);

        return (development, commit) switch
        {
            (true, { } c) => string.Format(CultureInfo.CurrentCulture, UiStrings.Settings_VersionDevWithCommit, version, c),
            (true, null) => string.Format(CultureInfo.CurrentCulture, UiStrings.Settings_VersionDev, version),
            (false, { } c) => string.Format(CultureInfo.CurrentCulture, UiStrings.Settings_VersionWithCommit, version, c),
            _ => version,
        };
    }

    // Los metadatos de compilación pueden traer varios campos separados por puntos: el commit es el último
    private static string? ShortCommit(string metadata)
    {
        var last = metadata.Split('.').LastOrDefault(p => p.Length > 0)?.Trim() ?? "";
        if (last.Length < ShortCommitLength || !last.All(Uri.IsHexDigit))
        {
            return null;
        }

        return last[..ShortCommitLength].ToLowerInvariant();
    }
}

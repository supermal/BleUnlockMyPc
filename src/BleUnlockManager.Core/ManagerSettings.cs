using System.Text.Json;
using System.Text.Json.Serialization;

namespace BleUnlockManager.Core;

/// <summary>Preferencia de tema del gestor.</summary>
public enum ThemePreference
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Preferencias del propio gestor (%LocalAppData%\BleUnlockManager\settings.json). Formato
/// compatible con la versión WinUI: {"Theme":"System|Light|Dark"}. Un fichero ausente o dañado
/// equivale a los valores por defecto; nunca lanza al leer.
/// </summary>
public sealed class ManagerSettings
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public ThemePreference Theme { get; set; } = ThemePreference.System;

    public static ManagerSettings Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ManagerSettings>(File.ReadAllText(path), Options) ?? new ManagerSettings()
                : new ManagerSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new ManagerSettings();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }
}

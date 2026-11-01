using System.Text.Json;
using System.Text.Json.Serialization;

namespace BleUnlockService.Config;

// Resultado de la última recarga en caliente, para que el gestor confirme el guardado sin leer el
// texto del registro (que ahora depende del idioma). Result: applied | rejected | unchanged.
// Id cambia en cada escritura: el gestor espera a ver uno distinto del que había antes de guardar
public sealed record ReloadStatus(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("result")] string Result,
    [property: JsonPropertyName("deviceChanged")] bool DeviceChanged,
    [property: JsonPropertyName("credentialsChanged")] bool CredentialsChanged,
    [property: JsonPropertyName("message")] string? Message)
{
    public static ReloadStatus Create(string result, bool deviceChanged = false, bool credentialsChanged = false,
        string? message = null) =>
        new(Guid.NewGuid().ToString("N"), result, deviceChanged, credentialsChanged, message);
}

public static class ReloadStatusFile
{
    public const string Applied = "applied";
    public const string Rejected = "rejected";
    public const string Unchanged = "unchanged";

    // Fichero temporal y reemplazo: el gestor nunca lee un JSON a medias
    public static void Write(string path, ReloadStatus status)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(status));
        File.Move(temp, path, overwrite: true);
    }
}

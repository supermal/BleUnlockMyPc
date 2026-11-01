using System.Diagnostics;
using System.Text.Json;

namespace BleUnlockManager.Core.Config;

/// <summary>Resultado de la recarga en caliente.</summary>
public enum ConfigReloadStatus
{
    /// <summary>El servicio aplicó la configuración nueva (o no había nada que cambiar).</summary>
    Reloaded,

    /// <summary>El servicio la rechazó y conservó la anterior.</summary>
    Rejected,

    /// <summary>Sin confirmación en el plazo (servicio detenido o fichero de estado inaccesible).</summary>
    TimedOut,
}

/// <summary>Resultado de la espera. <paramref name="Message"/>: motivo del rechazo, en el idioma del servicio.</summary>
public sealed record ConfigReloadOutcome(
    ConfigReloadStatus Status,
    bool? DeviceChanged = null,
    bool? CredentialsChanged = null,
    string? Message = null);

/// <summary>Id del último estado escrito antes de guardar (null si el fichero no existe).</summary>
public sealed record ConfigReloadCheckpoint(string? Id);

/// <summary>
/// Confirmación de la recarga: el servicio escribe reload-status.json en cada recarga con un id nuevo;
/// tras guardar se espera a ver un id distinto del anotado antes. No depende del texto del registro,
/// que cambia con el idioma.
/// </summary>
public static class ConfigReloadWatcher
{
    /// <summary>Intervalo de sondeo.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    public static ConfigReloadCheckpoint CaptureCheckpoint(string statusFile) => new(TryRead(statusFile)?.Id);

    public static async Task<ConfigReloadOutcome> WaitAsync(
        string statusFile, ConfigReloadCheckpoint checkpoint, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (TryRead(statusFile) is { } status && status.Id != checkpoint.Id)
            {
                return status.Result switch
                {
                    "rejected" => new ConfigReloadOutcome(ConfigReloadStatus.Rejected, Message: status.Message),
                    _ => new ConfigReloadOutcome(ConfigReloadStatus.Reloaded, status.DeviceChanged, status.CredentialsChanged),
                };
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        return new ConfigReloadOutcome(ConfigReloadStatus.TimedOut);
    }

    // Ausente, bloqueado o a medio escribir: null, y se vuelve a intentar en el siguiente sondeo
    private static StatusDto? TryRead(string path)
    {
        try
        {
            var status = JsonSerializer.Deserialize<StatusDto>(File.ReadAllText(path), Options);
            return string.IsNullOrEmpty(status?.Id) ? null : status;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private sealed record StatusDto(string? Id, string? Result, bool DeviceChanged, bool CredentialsChanged, string? Message);
}

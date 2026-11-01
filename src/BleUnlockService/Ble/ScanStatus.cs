namespace BleUnlockService.Ble;

// Estado del escaneo: Code es estable y en inglés (lo interpreta el gestor), Text es el texto para
// el registro en el idioma del servicio, Detail el mensaje original del error si lo hay
public sealed record ScanStatus(string Code, string Text, string? Detail = null)
{
    public const string Running = "running";
    public const string NoAdapter = "noAdapter";
    public const string RadioOff = "radioOff";
    public const string StartFailed = "startFailed";
    // El escaneo dice estar en marcha pero no llega ningún anuncio: recuperación automática en curso
    public const string Stalled = "stalled";
    // La recuperación automática no funcionó: hace falta que el usuario apague y encienda el Bluetooth
    public const string StalledFailed = "stalledFailed";
}

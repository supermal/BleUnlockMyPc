namespace BleUnlockService.Config;

// Qué le falta a una configuración válida para funcionar del todo. En un equipo recién instalado no hay
// dispositivo ni credenciales: el servicio debe arrancar igual, porque la lista de dispositivos
// emparejados que necesita el gestor para elegir uno la sirve el propio servicio
public enum SetupState
{
    Complete,
    // Sin dispositivo: no se vigila a nadie, ni se bloquea ni se desbloquea
    NoDevice,
    // Con dispositivo pero sin usuario o clave: se bloquea al irse, nunca se desbloquea
    NoCredentials
}

public static class ConfigCompleteness
{
    // IRK de relleno que escribe el gestor al desemparejar el dispositivo en uso: no es un dispositivo
    public const string PlaceholderIrk = "00000000000000000000000000000000";

    // Sobre valores ya descifrados (OptionsSecrets)
    public static bool HasDevice(DeviceOptions device) =>
        OptionsValidator.NormalizeIrk(device.Irk) is { } irk && irk != PlaceholderIrk
        || OptionsValidator.NormalizeAddress(device.IdentityAddress) is not null;

    public static bool HasCredentials(CredentialOptions credentials) =>
        !string.IsNullOrWhiteSpace(credentials.UserName) && !string.IsNullOrEmpty(credentials.Password);

    public static SetupState Evaluate(BleUnlockOptions options) =>
        !HasDevice(options.Device) ? SetupState.NoDevice
        : !HasCredentials(options.Credentials) ? SetupState.NoCredentials
        : SetupState.Complete;

    // Código estable para el canal de gestión; null = completa
    public static string? ToWire(SetupState state) => state switch
    {
        SetupState.NoDevice => "noDevice",
        SetupState.NoCredentials => "noCredentials",
        _ => null
    };
}

namespace AutoUnlockService.Ble;

// Una emision BLE recibida; inmutable para poder cruzar hilos sin bloqueo
public sealed record AdvertisementSample(
    ulong Address,
    bool IsRandomAddress,
    short Rssi,
    string? LocalName,
    DateTimeOffset Timestamp);

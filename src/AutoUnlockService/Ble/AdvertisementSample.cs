namespace AutoUnlockService.Ble;

// Una emision BLE recibida; inmutable para poder cruzar hilos sin bloqueo.
// Sin LocalName a proposito: el emparejamiento no lo usa y leerlo en cada emision de cada
// dispositivo cercano supone una llamada WinRT y una cadena por emision
public sealed record AdvertisementSample(
    ulong Address,
    bool IsRandomAddress,
    short Rssi,
    DateTimeOffset Timestamp);

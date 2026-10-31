namespace AutoUnlockService.Ble;

// Abstraccion del origen de muestras: permite mover el escaneo a otro proceso sin tocar la maquina de estados
public interface IRssiSource
{
    event Action<AdvertisementSample>? SampleReceived;
    event Action<ScanStatus>? StatusChanged;

    bool IsAvailable { get; }

    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}

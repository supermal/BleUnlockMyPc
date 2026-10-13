using AutoUnlockService.Ble;

namespace AutoUnlockService;

// Verificacion temporal: imprime cuantas emisiones llegan. En la Task 15 se sustituye por el orquestador real
public sealed class Worker(ILogger<Worker> log, IRssiSource source, TimeProvider time) : BackgroundService
{
    private int _sampleCount;
    private readonly HashSet<ulong> _addresses = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        source.SampleReceived += OnSample;
        source.StatusChanged += s => log.LogInformation("BLE 状态：{Status}", s);
        await source.StartAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), time, stoppingToken); }
            catch (OperationCanceledException) { break; }

            int devices;
            lock (_addresses) { devices = _addresses.Count; _addresses.Clear(); }
            log.LogInformation("摘要：30 秒内收到 {Count} 条广播，来自 {Devices} 个不同地址",
                Interlocked.Exchange(ref _sampleCount, 0), devices);
        }

        source.SampleReceived -= OnSample;
        await source.StopAsync();
    }

    private void OnSample(AdvertisementSample s)
    {
        Interlocked.Increment(ref _sampleCount);
        lock (_addresses) { _addresses.Add(s.Address); }
        log.LogDebug("广播 {Address:X12} RSSI={Rssi} 随机={Random} 名称={Name}",
            s.Address, s.Rssi, s.IsRandomAddress, s.LocalName ?? "-");
    }
}

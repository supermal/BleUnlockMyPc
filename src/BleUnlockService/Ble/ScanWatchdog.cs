namespace BleUnlockService.Ble;

// Lo que el escáner debe hacer tras cada comprobación del perro guardián
public enum WatchdogAction
{
    None,
    // Primera detección: avisar y reiniciar el escaneo
    Stalled,
    // Reintento tras un fallo anterior: reiniciar el escaneo sin volver a avisar
    RestartScan,
    // Reiniciar el escaneo no bastó: reiniciar el adaptador Bluetooth
    RestartAdapter,
    // Primera vez que la recuperación automática no funciona: avisar al usuario
    Failed,
    // Vuelven a llegar anuncios
    Recovered,
}

// Detecta el receptor Bluetooth «colgado»: Windows dice que el escaneo sigue en marcha, pero no llega
// ningún anuncio de ningún dispositivo (visto el 2026-11-01 tras un tiempo de espera del controlador
// BTHUSB). El evento Stopped no se dispara en ese caso, así que la única señal es el silencio total.
// Escalado: reiniciar el escaneo → reiniciar el adaptador → avisar del fallo y reintentar cada 5 min.
// Sin E/S: el escáner ejecuta las acciones, esta clase solo decide (y así se puede probar).
public sealed class ScanWatchdog(TimeProvider time)
{
    // Con dispositivos alrededor llegan decenas de anuncios por segundo: 30 s sin ninguno es una avería
    public static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(30);
    // El adaptador tarda en volver a enumerarse tras reiniciarlo
    public static readonly TimeSpan AdapterRestartWait = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    private enum Phase { Healthy, ScanRestarted, AdapterRestarted, Failed }

    private long _lastAdvertTicks;   // escrito desde el hilo de recepción de anuncios
    private DateTimeOffset _baseline = time.GetUtcNow();
    private Phase _phase = Phase.Healthy;
    private DateTimeOffset _phaseSince;
    private bool _failureReported;

    public bool IsStalled => _phase != Phase.Healthy;

    // Ruta caliente (cada anuncio de cada dispositivo cercano): solo una escritura atómica
    public void NoteAdvertisement() => Volatile.Write(ref _lastAdvertTicks, time.GetUtcNow().UtcTicks);

    // Un watcher recién arrancado tiene su propio plazo de 30 s. Durante una recuperación no se
    // reinicia el plazo: los reinicios forman parte de ella y no deben ocultar que sigue sin llegar nada
    public void NoteWatcherStarted()
    {
        if (_phase == Phase.Healthy) _baseline = time.GetUtcNow();
    }

    // scanning: el watcher está arrancado. Solo se detecta una avería nueva mientras escanea; una
    // recuperación en curso sigue su calendario aunque el adaptador desaparezca un momento
    public WatchdogAction Check(bool scanning)
    {
        var now = time.GetUtcNow();
        var lastAdvert = new DateTimeOffset(Volatile.Read(ref _lastAdvertTicks), TimeSpan.Zero);

        if (_phase == Phase.Healthy)
        {
            if (!scanning) return WatchdogAction.None;
            var lastSign = lastAdvert > _baseline ? lastAdvert : _baseline;
            if (now - lastSign < StallAfter) return WatchdogAction.None;
            Enter(Phase.ScanRestarted, now);
            return WatchdogAction.Stalled;
        }

        if (lastAdvert > _phaseSince)
        {
            _phase = Phase.Healthy;
            _failureReported = false;
            _baseline = now;
            return WatchdogAction.Recovered;
        }

        var elapsed = now - _phaseSince;
        switch (_phase)
        {
            case Phase.ScanRestarted when elapsed >= StallAfter:
                Enter(Phase.AdapterRestarted, now);
                return WatchdogAction.RestartAdapter;

            case Phase.AdapterRestarted when elapsed >= AdapterRestartWait:
                Enter(Phase.Failed, now);
                if (_failureReported) return WatchdogAction.None;
                _failureReported = true;
                return WatchdogAction.Failed;

            case Phase.Failed when elapsed >= RetryAfterFailure:
                Enter(Phase.ScanRestarted, now);
                return WatchdogAction.RestartScan;

            default:
                return WatchdogAction.None;
        }
    }

    private void Enter(Phase phase, DateTimeOffset now)
    {
        _phase = phase;
        _phaseSince = now;
    }
}

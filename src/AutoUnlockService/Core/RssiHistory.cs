namespace AutoUnlockService.Core;

public readonly record struct WindowStats(int Count, double Average);

// Cola de muestras con poda por antiguedad. No asume que las marcas de tiempo
// lleguen ordenadas: el callback de WinRT no lo garantiza
public sealed class RssiHistory(TimeSpan retention)
{
    private const int PruneThreshold = 16;

    private readonly List<(DateTimeOffset Time, short Rssi)> _points = [];
    private readonly object _lock = new();
    private DateTimeOffset _newestSeen = DateTimeOffset.MinValue;

    public bool HasEverSeenDevice { get; private set; }

    public void Add(short rssi, DateTimeOffset at)
    {
        lock (_lock)
        {
            HasEverSeenDevice = true;
            _points.Add((at, rssi));
            if (at > _newestSeen) _newestSeen = at;

            // La poda es relativa a la muestra mas reciente vista, no a la recien anadida:
            // asi una muestra desordenada no borra el historico valido.
            // El umbral evita recorrer la lista en cada emision recibida
            if (_points.Count > PruneThreshold)
            {
                var cutoff = _newestSeen - retention;
                _points.RemoveAll(p => p.Time < cutoff);
            }
        }
    }

    public WindowStats InWindow(DateTimeOffset now, TimeSpan window)
    {
        var from = now - window;
        lock (_lock)
        {
            int count = 0;
            double sum = 0;
            foreach (var p in _points)
            {
                // El borde antiguo es exclusivo y el instante actual inclusivo;
                // las marcas futuras se descartan por si el reloj retrocedio
                if (p.Time > from && p.Time <= now)
                {
                    count++;
                    sum += p.Rssi;
                }
            }
            return new WindowStats(count, count == 0 ? 0 : sum / count);
        }
    }

    // Tras despertar de suspension el historico ya no es representativo, pero el
    // dispositivo si existio: esa distincion la necesita la maquina de estados
    public void Clear()
    {
        lock (_lock)
        {
            _points.Clear();
            _newestSeen = DateTimeOffset.MinValue;
        }
    }

    // Se usa al cambiar de dispositivo: hay que olvidarlo todo
    public void Reset()
    {
        lock (_lock)
        {
            _points.Clear();
            _newestSeen = DateTimeOffset.MinValue;
            HasEverSeenDevice = false;
        }
    }
}

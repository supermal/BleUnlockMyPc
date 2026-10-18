namespace AutoUnlockService.Core;

public readonly record struct WindowStats(int Count, double Average);

// Cola de muestras con poda por antiguedad. No asume que las marcas de tiempo
// lleguen ordenadas: el callback de WinRT no lo garantiza
public sealed class RssiHistory(TimeSpan retention, TimeProvider time)
{
    private const int PruneThreshold = 16;

    private readonly List<(DateTimeOffset Time, short Rssi)> _points = [];
    private readonly object _lock = new();

    public bool HasEverSeenDevice { get; private set; }

    public void Add(short rssi, DateTimeOffset at)
    {
        lock (_lock)
        {
            HasEverSeenDevice = true;

            _points.Add((at, rssi));

            // La poda es relativa al reloj actual, no a las marcas de las muestras: un hueco
            // largo sin emisiones o una marca adelantada no pueden envenenar la base de poda,
            // y una muestra desordenada no borra historico valido.
            // El umbral evita recorrer la lista en cada emision recibida
            if (_points.Count > PruneThreshold)
            {
                var cutoff = time.GetUtcNow() - retention;
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

    // 自水印之后到达的样本，按时间升序（批次判定按到达顺序消费）。
    // 时间戳可能乱序到达（WinRT 不保证顺序），所以这里排序后再返回
    public IReadOnlyList<(DateTimeOffset Time, short Rssi)> Since(DateTimeOffset after)
    {
        lock (_lock)
        {
            var result = new List<(DateTimeOffset Time, short Rssi)>();
            foreach (var p in _points)
                if (p.Time > after)
                    result.Add(p);

            result.Sort(static (a, b) => a.Time.CompareTo(b.Time));
            return result;
        }
    }

    // Tras despertar de suspension el historico ya no es representativo, pero el
    // dispositivo si existio: esa distincion la necesita la maquina de estados
    public void Clear()
    {
        lock (_lock)
        {
            _points.Clear();
        }
    }

    // Se usa al cambiar de dispositivo: hay que olvidarlo todo
    public void Reset()
    {
        lock (_lock)
        {
            _points.Clear();
            HasEverSeenDevice = false;
        }
    }
}

namespace AutoUnlockService;

public sealed class RssiSmoother
{
    private readonly int[] _buffer;
    private int _index;
    private int _count;

    public RssiSmoother(int windowSize)
    {
        _buffer = new int[windowSize];
    }

    public double Add(int rssi)
    {
        _buffer[_index] = rssi;
        _index = (_index + 1) % _buffer.Length;
        if (_count < _buffer.Length) _count++;

        double sum = 0;
        for (int i = 0; i < _count; i++) sum += _buffer[i];
        return sum / _count;
    }

    public void Reset()
    {
        _index = 0;
        _count = 0;
    }
}

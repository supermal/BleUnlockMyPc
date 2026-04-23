using System.IO.Ports;
using Microsoft.Extensions.Logging;

namespace AutoUnlockService;

public sealed class RssiMonitor : IDisposable
{
    private SerialPort? _port;
    private readonly ILogger<RssiMonitor>? _logger;
    internal string _buffer = string.Empty;

    public event Action<int>? RssiReceived;

    // Production constructor
    public RssiMonitor(string comPort, int baudRate, ILogger<RssiMonitor> logger)
    {
        _logger = logger;
        _port = new SerialPort(comPort, baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 500,
            WriteTimeout = 500
        };
        _port.DataReceived += OnDataReceived;
    }

    // Test constructor (no serial port)
    internal RssiMonitor() { }

    public void Start() => _port!.Open();
    public void Stop() { if (_port?.IsOpen == true) _port.Close(); }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            _buffer += _port!.ReadExisting();
            ProcessBuffer();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Serial read error");
        }
    }

    internal void InjectBuffer(string data) => _buffer += data;

    internal void ProcessBuffer()
    {
        while (true)
        {
            int end = _buffer.IndexOf("\r\n", StringComparison.Ordinal);
            if (end < 0) break;

            string line = _buffer[..end];
            _buffer = _buffer[(end + 2)..];

            if (TryParseRssi(line, out int rssi))
                RssiReceived?.Invoke(rssi);
        }
    }

    internal static bool TryParseRssi(string line, out int rssi)
    {
        rssi = 0;
        if (!line.StartsWith("+RSSI:", StringComparison.OrdinalIgnoreCase)) return false;
        return int.TryParse(line[6..], out rssi);
    }

    public void Dispose() => _port?.Dispose();
}

using AutoUnlockService;
using Xunit;

namespace AutoUnlockService.Tests;

public class RssiMonitorParserTests
{
    [Theory]
    [InlineData("+RSSI:-65", -65)]
    [InlineData("+RSSI:-100", -100)]
    [InlineData("+rssi:-45", -45)]
    [InlineData("+RSSI:0", 0)]
    public void TryParseRssi_ValidLine_ReturnsTrue(string line, int expected)
    {
        Assert.True(RssiMonitor.TryParseRssi(line, out int rssi));
        Assert.Equal(expected, rssi);
    }

    [Theory]
    [InlineData("")]
    [InlineData("+RSSI:")]
    [InlineData("+RSSI:abc")]
    [InlineData("AT+OK")]
    public void TryParseRssi_InvalidLine_ReturnsFalse(string line)
    {
        Assert.False(RssiMonitor.TryParseRssi(line, out _));
    }

    [Fact]
    public void ProcessBuffer_SingleCompletePacket_FiresEvent()
    {
        var monitor = new RssiMonitor();
        int received = 0;
        int lastRssi = 0;
        monitor.RssiReceived += rssi => { received++; lastRssi = rssi; };

        monitor.InjectBuffer("+RSSI:-65\r\n");
        monitor.ProcessBuffer();

        Assert.Equal(1, received);
        Assert.Equal(-65, lastRssi);
    }

    [Fact]
    public void ProcessBuffer_TwoPacketsInOneChunk_FiresTwice()
    {
        var monitor = new RssiMonitor();
        var values = new List<int>();
        monitor.RssiReceived += rssi => values.Add(rssi);

        monitor.InjectBuffer("+RSSI:-65\r\n+RSSI:-70\r\n");
        monitor.ProcessBuffer();

        Assert.Equal(new[] { -65, -70 }, values);
    }

    [Fact]
    public void ProcessBuffer_HalfPacket_DoesNotFire()
    {
        var monitor = new RssiMonitor();
        int received = 0;
        monitor.RssiReceived += _ => received++;

        monitor.InjectBuffer("+RSSI:-65");
        monitor.ProcessBuffer();

        Assert.Equal(0, received);
    }

    [Fact]
    public void ProcessBuffer_HalfThenRest_FiresOnce()
    {
        var monitor = new RssiMonitor();
        int received = 0;
        monitor.RssiReceived += _ => received++;

        monitor.InjectBuffer("+RSSI:-65");
        monitor.ProcessBuffer();
        monitor.InjectBuffer("\r\n");
        monitor.ProcessBuffer();

        Assert.Equal(1, received);
    }
}

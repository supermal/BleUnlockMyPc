using AutoUnlockService.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AutoUnlockService.Tests;

public class SampleIntervalGateTests
{
    [Fact]
    public void Interval_cero_acepta_todas_las_lecturas()
    {
        var time = new FakeTimeProvider();
        var gate = new SampleIntervalGate(time);

        Assert.True(gate.ShouldAccept(0));
        Assert.True(gate.ShouldAccept(0));
        Assert.True(gate.ShouldAccept(0));
    }

    [Fact]
    public void Intervalo_positivo_limita_por_tiempo()
    {
        var time = new FakeTimeProvider();
        var gate = new SampleIntervalGate(time);

        Assert.True(gate.ShouldAccept(500));
        time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.False(gate.ShouldAccept(500));
        time.Advance(TimeSpan.FromMilliseconds(299));
        Assert.False(gate.ShouldAccept(500));
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(gate.ShouldAccept(500));
    }

    [Fact]
    public void Reset_permite_aceptar_de_inmediato()
    {
        var time = new FakeTimeProvider();
        var gate = new SampleIntervalGate(time);

        Assert.True(gate.ShouldAccept(2000));
        Assert.False(gate.ShouldAccept(2000));

        gate.Reset();

        Assert.True(gate.ShouldAccept(2000));
    }
}

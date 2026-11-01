using BleUnlockService.Ble;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BleUnlockService.Tests;

public class ScanWatchdogTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(5);

    private static (FakeTimeProvider time, ScanWatchdog dog) Started()
    {
        var time = new FakeTimeProvider();
        var dog = new ScanWatchdog(time);
        dog.NoteWatcherStarted();
        return (time, dog);
    }

    // Avanza en pasos de 5 s (como el bucle real) y devuelve la primera acción distinta de None
    private static WatchdogAction RunUntilAction(FakeTimeProvider time, ScanWatchdog dog, TimeSpan max, bool scanning = true)
    {
        for (var t = TimeSpan.Zero; t < max; t += Step)
        {
            time.Advance(Step);
            var action = dog.Check(scanning);
            if (action != WatchdogAction.None) return action;
        }

        return WatchdogAction.None;
    }

    [Fact]
    public void Con_anuncios_continuos_nunca_actua()
    {
        var (time, dog) = Started();
        for (var i = 0; i < 100; i++)
        {
            time.Advance(Step);
            dog.NoteAdvertisement();
            Assert.Equal(WatchdogAction.None, dog.Check(scanning: true));
        }

        Assert.False(dog.IsStalled);
    }

    [Fact]
    public void Treinta_segundos_sin_ningun_anuncio_es_una_averia()
    {
        var (time, dog) = Started();
        dog.NoteAdvertisement();

        time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(WatchdogAction.None, dog.Check(true));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(WatchdogAction.Stalled, dog.Check(true));
        Assert.True(dog.IsStalled);
    }

    [Fact]
    public void Un_watcher_recien_arrancado_tiene_su_propio_plazo()
    {
        var time = new FakeTimeProvider();
        var dog = new ScanWatchdog(time);
        time.Advance(TimeSpan.FromMinutes(10));     // largo rato sin escanear (Bluetooth apagado)
        dog.NoteWatcherStarted();

        time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(WatchdogAction.None, dog.Check(true));
    }

    [Fact]
    public void Sin_escaneo_activo_no_detecta_averias_nuevas()
    {
        var (time, dog) = Started();
        Assert.Equal(WatchdogAction.None, RunUntilAction(time, dog, TimeSpan.FromMinutes(5), scanning: false));
        Assert.False(dog.IsStalled);
    }

    [Fact]
    public void Escalado_completo_reinicia_escaneo_luego_adaptador_y_luego_avisa()
    {
        var (time, dog) = Started();

        Assert.Equal(WatchdogAction.Stalled, RunUntilAction(time, dog, TimeSpan.FromMinutes(1)));
        Assert.Equal(WatchdogAction.RestartAdapter, RunUntilAction(time, dog, ScanWatchdog.StallAfter));
        // Durante el reinicio del adaptador el escaneo puede no estar disponible: el calendario sigue
        Assert.Equal(WatchdogAction.Failed, RunUntilAction(time, dog, ScanWatchdog.AdapterRestartWait, scanning: false));
        Assert.True(dog.IsStalled);
    }

    [Fact]
    public void Tras_el_fallo_reintenta_cada_cinco_minutos_sin_volver_a_avisar()
    {
        var (time, dog) = Started();
        RunUntilAction(time, dog, TimeSpan.FromMinutes(1));     // Stalled
        RunUntilAction(time, dog, TimeSpan.FromMinutes(1));     // RestartAdapter
        RunUntilAction(time, dog, TimeSpan.FromMinutes(2));     // Failed

        time.Advance(ScanWatchdog.RetryAfterFailure - Step);
        Assert.Equal(WatchdogAction.None, dog.Check(true));
        Assert.Equal(WatchdogAction.RestartScan, RunUntilAction(time, dog, TimeSpan.FromMinutes(1)));
        Assert.Equal(WatchdogAction.RestartAdapter, RunUntilAction(time, dog, TimeSpan.FromMinutes(1)));
        // Segundo fallo seguido: ya se avisó, no se repite el aviso
        Assert.Equal(WatchdogAction.None, RunUntilAction(time, dog, ScanWatchdog.AdapterRestartWait + Step));
        Assert.True(dog.IsStalled);
    }

    [Theory]
    [InlineData(1)]   // tras reiniciar el escaneo
    [InlineData(2)]   // tras reiniciar el adaptador
    [InlineData(3)]   // tras avisar del fallo
    public void Un_anuncio_en_cualquier_fase_de_la_recuperacion_la_da_por_terminada(int phasesToGo)
    {
        var (time, dog) = Started();
        for (var i = 0; i < phasesToGo; i++)
            RunUntilAction(time, dog, TimeSpan.FromMinutes(2));

        time.Advance(Step);
        dog.NoteAdvertisement();
        Assert.Equal(WatchdogAction.Recovered, dog.Check(true));
        Assert.False(dog.IsStalled);

        // Y vuelve a vigilar desde cero: 30 s nuevos antes de otra avería
        time.Advance(TimeSpan.FromSeconds(25));
        Assert.Equal(WatchdogAction.None, dog.Check(true));
    }

    [Fact]
    public void Tras_recuperarse_una_nueva_averia_vuelve_a_avisar()
    {
        var (time, dog) = Started();
        RunUntilAction(time, dog, TimeSpan.FromMinutes(1));     // Stalled
        RunUntilAction(time, dog, TimeSpan.FromMinutes(1));     // RestartAdapter
        RunUntilAction(time, dog, TimeSpan.FromMinutes(2));     // Failed
        time.Advance(Step);
        dog.NoteAdvertisement();
        Assert.Equal(WatchdogAction.Recovered, dog.Check(true));

        Assert.Equal(WatchdogAction.Stalled, RunUntilAction(time, dog, TimeSpan.FromMinutes(1)));
        RunUntilAction(time, dog, TimeSpan.FromMinutes(1));     // RestartAdapter
        Assert.Equal(WatchdogAction.Failed, RunUntilAction(time, dog, TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void Un_reinicio_del_watcher_durante_la_recuperacion_no_reinicia_el_plazo()
    {
        var (time, dog) = Started();
        RunUntilAction(time, dog, TimeSpan.FromMinutes(1));     // Stalled
        time.Advance(TimeSpan.FromSeconds(20));
        dog.NoteWatcherStarted();                               // el reinicio que pidió el propio perro
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(WatchdogAction.RestartAdapter, dog.Check(true));
    }

    [Theory]
    [InlineData(@"\\?\USB#VID_8087&PID_0026#5&261d42b0&0&10#{92383b0e-f90e-4ac9-8d44-8c2d0d0ebda2}", @"USB\VID_8087&PID_0026\5&261d42b0&0&10")]
    [InlineData(@"USB\VID_8087&PID_0026\5&261d42b0&0&10", @"USB\VID_8087&PID_0026\5&261d42b0&0&10")]
    [InlineData("", null)]
    public void El_id_del_adaptador_se_convierte_en_id_de_instancia(string deviceId, string? expected)
    {
        Assert.Equal(expected, BluetoothAdapterRestarter.ToInstanceId(deviceId));
    }
}

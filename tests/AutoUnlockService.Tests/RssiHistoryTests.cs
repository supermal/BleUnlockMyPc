using AutoUnlockService.Core;
using Xunit;

namespace AutoUnlockService.Tests;

public class RssiHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Retention = TimeSpan.FromSeconds(120);

    [Fact]
    public void InWindow_sin_muestras_devuelve_cuenta_cero()
    {
        var h = new RssiHistory(Retention);
        Assert.Equal(0, h.InWindow(T0, TimeSpan.FromSeconds(10)).Count);
    }

    [Fact]
    public void InWindow_promedia_solo_las_muestras_dentro_de_la_ventana()
    {
        var h = new RssiHistory(Retention);
        h.Add(-40, T0.AddSeconds(-20));   // fuera de una ventana de 10 s
        h.Add(-50, T0.AddSeconds(-5));
        h.Add(-60, T0.AddSeconds(-1));

        var stats = h.InWindow(T0, TimeSpan.FromSeconds(10));

        Assert.Equal(2, stats.Count);
        Assert.Equal(-55, stats.Average, precision: 6);
    }

    [Fact]
    public void InWindow_excluye_el_borde_antiguo_e_incluye_el_instante_actual()
    {
        var h = new RssiHistory(Retention);
        h.Add(-40, T0.AddSeconds(-10));   // exactamente en el borde: excluida
        h.Add(-60, T0);                   // justo ahora: incluida

        var stats = h.InWindow(T0, TimeSpan.FromSeconds(10));

        Assert.Equal(1, stats.Count);
        Assert.Equal(-60, stats.Average, precision: 6);
    }

    [Fact]
    public void Add_acepta_marcas_de_tiempo_desordenadas()
    {
        var h = new RssiHistory(Retention);
        // El callback de WinRT no garantiza orden: llega primero la mas reciente
        h.Add(-50, T0.AddSeconds(-2));
        h.Add(-70, T0.AddSeconds(-8));
        h.Add(-60, T0.AddSeconds(-5));

        var stats = h.InWindow(T0, TimeSpan.FromSeconds(10));

        Assert.Equal(3, stats.Count);
        Assert.Equal(-60, stats.Average, precision: 6);
    }

    [Fact]
    public void Add_acepta_marcas_de_tiempo_repetidas()
    {
        var h = new RssiHistory(Retention);
        h.Add(-50, T0.AddSeconds(-1));
        h.Add(-70, T0.AddSeconds(-1));

        var stats = h.InWindow(T0, TimeSpan.FromSeconds(10));

        Assert.Equal(2, stats.Count);
        Assert.Equal(-60, stats.Average, precision: 6);
    }

    [Fact]
    public void Add_descarta_muestras_mas_viejas_que_la_retencion()
    {
        var h = new RssiHistory(TimeSpan.FromSeconds(30));
        h.Add(-50, T0);
        // Muestras caducadas que llegan despues de la reciente; la 16a supera el umbral de poda
        for (int i = 0; i < 16; i++) h.Add(-50, T0.AddSeconds(-300 - i));

        // Justo tras disparar la poda solo debe quedar la reciente
        Assert.Equal(1, h.InWindow(T0, TimeSpan.FromSeconds(600)).Count);
    }

    [Fact]
    public void Add_poda_respecto_a_la_marca_mas_reciente_vista_y_no_a_la_recien_anadida()
    {
        var h = new RssiHistory(TimeSpan.FromSeconds(30));
        for (int i = 0; i < 20; i++) h.Add(-50, T0.AddSeconds(-i));

        h.Add(-90, T0.AddSeconds(-100));   // llega tarde y ya esta fuera de la retencion
        // Si la poda usara la marca recien anadida, la caducada seguiria aqui
        Assert.Equal(20, h.InWindow(T0, TimeSpan.FromSeconds(600)).Count);

        h.Add(-60, T0.AddSeconds(-25));    // llega tarde pero dentro de la retencion: se conserva
        Assert.Equal(21, h.InWindow(T0, TimeSpan.FromSeconds(600)).Count);
    }

    [Fact]
    public void Add_marca_futura_atipica_no_provoca_la_poda_de_muestras_normales_posteriores()
    {
        var h = new RssiHistory(TimeSpan.FromSeconds(30));
        for (int i = 0; i < 20; i++) h.Add(-50, T0.AddSeconds(-i));

        h.Add(-30, T0.AddSeconds(1000));   // desfase de reloj: muy adelantada
        for (int i = 1; i <= 5; i++) h.Add(-50, T0.AddSeconds(i));

        Assert.Equal(25, h.InWindow(T0.AddSeconds(5), TimeSpan.FromSeconds(60)).Count);
    }

    [Fact]
    public void HasEverSeenDevice_es_falso_hasta_la_primera_muestra()
    {
        var h = new RssiHistory(Retention);
        Assert.False(h.HasEverSeenDevice);

        h.Add(-50, T0);
        Assert.True(h.HasEverSeenDevice);
    }

    [Fact]
    public void Clear_vacia_las_muestras_pero_recuerda_que_el_dispositivo_existio()
    {
        var h = new RssiHistory(Retention);
        h.Add(-50, T0);

        h.Clear();

        Assert.Equal(0, h.InWindow(T0, TimeSpan.FromSeconds(10)).Count);
        Assert.True(h.HasEverSeenDevice);
    }

    [Fact]
    public void Reset_olvida_tambien_que_el_dispositivo_existio()
    {
        var h = new RssiHistory(Retention);
        h.Add(-50, T0);

        h.Reset();

        Assert.False(h.HasEverSeenDevice);
    }

    [Fact]
    public void InWindow_ignora_muestras_con_marca_futura_tras_un_salto_de_reloj()
    {
        var h = new RssiHistory(Retention);
        h.Add(-50, T0.AddSeconds(5));   // el reloj retrocedio: muestra en el futuro

        Assert.Equal(0, h.InWindow(T0, TimeSpan.FromSeconds(10)).Count);
    }
}

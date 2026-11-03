using BleUnlockManager.Core.Logs;
using Xunit;

namespace BleUnlockManager.Tests;

/// <summary>Contador «sin leer» junto a «日志» (2026-11-03): errores por encima de advertencias, por hora.</summary>
public sealed class LogUnreadTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 20, 0, 0, TimeSpan.FromHours(1));

    private static LogEntry Entry(int second, LogSeverity severity, bool continuation = false) =>
        new("x", "x", continuation ? null : T0.AddSeconds(second), severity, continuation);

    private static readonly LogEntry[] Sample =
    [
        Entry(1, LogSeverity.Warning),
        Entry(2, LogSeverity.Error),
        Entry(3, LogSeverity.Information),
        Entry(4, LogSeverity.Warning),
        Entry(5, LogSeverity.Fatal),
        Entry(6, LogSeverity.Warning),
    ];

    [Fact]
    public void Sin_marca_todo_cuenta_como_sin_leer()
        => Assert.Equal(new LogUnreadCounts(2, 3), LogUnread.Count(Sample, null));

    [Fact]
    public void Solo_cuenta_lo_posterior_a_la_marca()
        => Assert.Equal(new LogUnreadCounts(1, 1), LogUnread.Count(Sample, T0.AddSeconds(4)));

    [Fact]
    public void Las_lineas_de_continuacion_no_cuentan()
        => Assert.True(LogUnread.Count([Entry(9, LogSeverity.Error, continuation: true)], null).IsEmpty);

    [Fact]
    public void Visto_hasta_es_la_entrada_mas_reciente_o_ahora_si_no_hay()
    {
        Assert.Equal(T0.AddSeconds(6), LogUnread.SeenUntil(Sample, T0.AddDays(1)));
        Assert.Equal(T0, LogUnread.SeenUntil([], T0));
    }

    [Fact]
    public void Tras_marcar_lo_visto_no_queda_nada_sin_leer()
        => Assert.True(LogUnread.Count(Sample, LogUnread.SeenUntil(Sample, T0)).IsEmpty);

    [Fact]
    public void Con_errores_el_contador_es_rojo_y_muestra_los_errores()
    {
        var badge = LogUnread.Badge(new LogUnreadCounts(2, 5));
        Assert.Equal("2", badge.Text);
        Assert.Equal("danger", badge.Tone);
        Assert.Contains("2", badge.ToolTip);
        Assert.Contains("5", badge.ToolTip);
    }

    [Fact]
    public void Solo_con_advertencias_el_contador_es_amarillo()
    {
        var badge = LogUnread.Badge(new LogUnreadCounts(0, 3));
        Assert.Equal("3", badge.Text);
        Assert.Equal("warning", badge.Tone);
    }

    [Fact]
    public void Sin_nada_sin_leer_el_contador_se_oculta()
        => Assert.Equal(LogUnreadBadge.Hidden, LogUnread.Badge(LogUnreadCounts.None));

    [Fact]
    public void La_marca_se_guarda_y_se_lee_igual()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bleunlock-seen-{Guid.NewGuid():N}.txt");
        try
        {
            Assert.Null(LogSeenStore.Load(path));
            LogSeenStore.Save(path, T0.AddSeconds(6));
            Assert.Equal(T0.AddSeconds(6), LogSeenStore.Load(path));

            File.WriteAllText(path, "no es una fecha");
            Assert.Null(LogSeenStore.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

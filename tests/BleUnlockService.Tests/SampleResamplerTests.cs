using BleUnlockService.Config;
using BleUnlockService.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BleUnlockService.Tests;

// Muestreo a intervalo fijo (2026-11-04): cada SampleIntervalMs se cuenta una lectura; sin anuncio nuevo
// se repite la anterior
public class SampleResamplerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 16, 0, 0, TimeSpan.FromHours(1));
    private static readonly TimeSpan NoLimit = TimeSpan.Zero;

    [Fact]
    public void Sin_ninguna_lectura_no_hay_nada_que_contar()
    {
        var sampler = new SampleResampler(new FakeTimeProvider(T0));

        Assert.Null(sampler.Take(holdAllowed: true, NoLimit));
    }

    [Fact]
    public void Se_cuenta_la_lectura_mas_reciente_del_intervalo()
    {
        var sampler = new SampleResampler(new FakeTimeProvider(T0));
        sampler.Offer(-70);
        sampler.Offer(-60);
        sampler.Offer(-65);

        Assert.Equal((short)-65, sampler.Take(holdAllowed: true, NoLimit));
    }

    [Fact]
    public void Sin_anuncio_nuevo_se_repite_la_anterior_en_cada_intervalo()
    {
        var sampler = new SampleResampler(new FakeTimeProvider(T0));
        sampler.Offer(-70);

        Assert.Equal((short)-70, sampler.Take(true, NoLimit));
        Assert.Equal((short)-70, sampler.Take(true, NoLimit));
        Assert.Equal((short)-70, sampler.Take(true, NoLimit));
    }

    [Fact]
    public void Con_el_receptor_colgado_solo_cuentan_lecturas_reales()
    {
        var sampler = new SampleResampler(new FakeTimeProvider(T0));
        sampler.Offer(-70);
        Assert.Equal((short)-70, sampler.Take(holdAllowed: false, NoLimit));

        Assert.Null(sampler.Take(holdAllowed: false, NoLimit));
    }

    [Fact]
    public void La_repeticion_se_detiene_tras_el_limite_sin_senal()
    {
        var time = new FakeTimeProvider(T0);
        var sampler = new SampleResampler(time);
        sampler.Offer(-60);
        sampler.Take(true, TimeSpan.FromSeconds(180));

        time.Advance(TimeSpan.FromSeconds(179));
        Assert.Equal((short)-60, sampler.Take(true, TimeSpan.FromSeconds(180)));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(sampler.Take(true, TimeSpan.FromSeconds(180)));

        // Una lectura real nueva reanuda la repetición
        sampler.Offer(-62);
        Assert.Equal((short)-62, sampler.Take(true, TimeSpan.FromSeconds(180)));
        Assert.Equal((short)-62, sampler.Take(true, TimeSpan.FromSeconds(180)));
    }

    [Fact]
    public void Tras_reiniciar_no_se_repite_una_lectura_antigua()
    {
        var sampler = new SampleResampler(new FakeTimeProvider(T0));
        sampler.Offer(-60);
        sampler.Reset();

        Assert.Null(sampler.Take(true, NoLimit));
    }

    // —— Efecto sobre las decisiones (comportamiento aceptado por el usuario) ——

    // Simula el bucle del Worker: cada intervalo, lo que dé el muestreador entra en el histórico
    private static StateChange? Run(ProximityStateMachine sm, SampleResampler sampler, RssiHistory history,
        FakeTimeProvider time, TimeSpan interval, TimeSpan duration)
    {
        var end = time.GetUtcNow() + duration;
        var nextEvaluate = time.GetUtcNow() + TimeSpan.FromSeconds(1);
        while (time.GetUtcNow() < end)
        {
            time.Advance(interval);
            if (sampler.Take(true, TimeSpan.FromSeconds(180)) is { } v) history.Add(v, time.GetUtcNow());
            if (time.GetUtcNow() >= nextEvaluate)
            {
                nextEvaluate += TimeSpan.FromSeconds(1);
                if (sm.Evaluate(history) is { } change) return change;
            }
        }

        return null;
    }

    [Fact]
    public void Una_lectura_baja_repetida_cada_300_ms_avisa_en_unos_dos_segundos()
    {
        var time = new FakeTimeProvider(T0);
        var history = new RssiHistory(TimeSpan.FromSeconds(120), time);
        var sm = new ProximityStateMachine(time, new ProximityOptions(), new NotificationOptions(), false);
        var sampler = new SampleResampler(time);
        time.Advance(TimeSpan.FromSeconds(20));
        history.Add(-50, time.GetUtcNow());
        sm.Evaluate(history);

        sampler.Offer(-90);
        var start = time.GetUtcNow();
        var change = Run(sm, sampler, history, time, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(10));

        Assert.Equal(ProximityAction.ShowLockPrompt, change?.Action);
        // Disparo + 5 copias = 6 × 300 ms = 1,8 s; la evaluación (cada segundo, en el primer paso de 300 ms
        // que lo alcanza) lo ve a los 2,1 s. Sin la repetición, una lectura sola nunca avisaría
        Assert.Equal(TimeSpan.FromSeconds(2.1), time.GetUtcNow() - start);
    }

    [Fact]
    public void Una_lectura_fuerte_repetida_cada_300_ms_desbloquea()
    {
        var time = new FakeTimeProvider(T0);
        var history = new RssiHistory(TimeSpan.FromSeconds(120), time);
        var sm = new ProximityStateMachine(time, new ProximityOptions(), new NotificationOptions(), true);
        var sampler = new SampleResampler(time);
        time.Advance(TimeSpan.FromSeconds(20));
        sampler.Offer(-90);
        Assert.Equal(ProximityState.LockedArmed,
            Run(sm, sampler, history, time, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(10))?.To);

        sampler.Offer(-40);
        var change = Run(sm, sampler, history, time, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(10));

        Assert.Equal(ProximityAction.SendCredentials, change?.Action);
    }
}

using AutoUnlockService.Notifications;
using AutoUnlockService.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AutoUnlockService.Tests;

public class ResilientNotifierTests
{
    private sealed class FakeNotifier : INotifier
    {
        public PromptOutcome Outcome { get; set; } = PromptOutcome.Reject;
        public Exception? Throws { get; set; }
        public int PromptCalls { get; private set; }
        public TimeSpan LastTimeout { get; private set; }
        public int CancelCalls { get; private set; }
        public List<string> Notifications { get; } = [];
        public TimeSpan DelayBeforeThrow { get; set; }
        public FakeTimeProvider? Time { get; set; }

        public async Task<PromptOutcome> PromptAsync(TimeSpan timeout, double snoozeMinutes, CancellationToken ct)
        {
            PromptCalls++;
            LastTimeout = timeout;
            if (Throws is not null)
            {
                if (DelayBeforeThrow > TimeSpan.Zero && Time is not null)
                    Time.Advance(DelayBeforeThrow);
                await Task.Yield();
                throw Throws;
            }
            return Outcome;
        }

        public Exception? NotifyThrows { get; set; }
        public Exception? CancelThrows { get; set; }

        public Task CancelAsync()
        {
            CancelCalls++;
            if (CancelThrows is not null) throw CancelThrows;
            return Task.CompletedTask;
        }

        public Task NotifyAsync(string message, CancellationToken ct)
        {
            if (NotifyThrows is not null) throw NotifyThrows;
            Notifications.Add(message);
            return Task.CompletedTask;
        }
    }

    private static (ResilientNotifier N, FakeNotifier Primary, FakeNotifier Fallback, FakeTimeProvider Time) Build()
    {
        var time = new FakeTimeProvider();
        var primary = new FakeNotifier { Time = time };
        var fallback = new FakeNotifier { Time = time };
        var n = new ResilientNotifier(primary, fallback, NullLogger<ResilientNotifier>.Instance, time);
        return (n, primary, fallback, time);
    }

    [Fact]
    public async Task Usa_el_primario_mientras_funcione()
    {
        var (n, primary, fallback, _) = Build();
        primary.Outcome = PromptOutcome.Snooze;

        var result = await n.PromptAsync(TimeSpan.FromSeconds(15), 5, default);

        Assert.Equal(PromptOutcome.Snooze, result);
        Assert.Equal(1, primary.PromptCalls);
        Assert.Equal(0, fallback.PromptCalls);
        Assert.False(n.UsingFallback);
    }

    [Fact]
    public async Task Si_el_ayudante_cae_reintenta_en_el_acto_con_el_respaldo()
    {
        var (n, primary, fallback, _) = Build();
        primary.Throws = new HelperUnavailableException("ayudante caido");
        fallback.Outcome = PromptOutcome.Reject;

        var result = await n.PromptAsync(TimeSpan.FromSeconds(15), 5, default);

        Assert.Equal(PromptOutcome.Reject, result);
        Assert.Equal(1, fallback.PromptCalls);
        Assert.True(n.UsingFallback);
    }

    [Fact]
    public async Task El_respaldo_recibe_el_tiempo_que_quedaba_no_el_plazo_entero()
    {
        var (n, primary, fallback, time) = Build();
        primary.Throws = new HelperUnavailableException("ayudante caido");
        primary.DelayBeforeThrow = TimeSpan.FromSeconds(6);

        await n.PromptAsync(TimeSpan.FromSeconds(15), 5, default);

        Assert.Equal(TimeSpan.FromSeconds(9), fallback.LastTimeout);
    }

    [Fact]
    public async Task Si_ya_no_queda_tiempo_el_respaldo_recibe_un_plazo_minimo()
    {
        var (n, primary, fallback, _) = Build();
        primary.Throws = new HelperUnavailableException("ayudante caido");
        primary.DelayBeforeThrow = TimeSpan.FromSeconds(30);

        await n.PromptAsync(TimeSpan.FromSeconds(15), 5, default);

        Assert.Equal(ResilientNotifier.MinimumFallbackTimeout, fallback.LastTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), ResilientNotifier.MinimumFallbackTimeout);
    }

    [Fact]
    public async Task El_suelo_amplia_tambien_un_plazo_original_mas_corto()
    {
        var (n, primary, fallback, _) = Build();
        primary.Throws = new HelperUnavailableException("ayudante caido");

        await n.PromptAsync(TimeSpan.FromSeconds(3), 5, default);

        Assert.Equal(TimeSpan.FromSeconds(5), fallback.LastTimeout);
    }

    [Fact]
    public async Task Notify_con_ayudante_caido_degrada_y_reenvia_por_el_respaldo()
    {
        var (n, primary, fallback, _) = Build();
        primary.NotifyThrows = new HelperUnavailableException("ayudante caido");

        await n.NotifyAsync("suspendido", default);

        Assert.True(n.UsingFallback);
        Assert.Equal(["suspendido"], fallback.Notifications);
    }

    [Fact]
    public async Task Cancel_con_ayudante_caido_degrada_sin_lanzar()
    {
        var (n, primary, fallback, _) = Build();
        primary.CancelThrows = new HelperUnavailableException("ayudante caido");

        await n.CancelAsync();

        Assert.True(n.UsingFallback);
        Assert.Equal(1, fallback.CancelCalls);
    }

    [Fact]
    public async Task Una_cancelacion_no_degrada_de_forma_permanente()
    {
        var (n, primary, fallback, _) = Build();
        primary.Throws = new HelperUnavailableException("ayudante caido");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => n.PromptAsync(TimeSpan.FromSeconds(15), 5, cts.Token));

        Assert.False(n.UsingFallback);
        Assert.Equal(0, fallback.PromptCalls);
    }

    [Fact]
    public void Degradar_desde_fuera_retira_lo_pendiente_del_primario()
    {
        var (n, primary, _, _) = Build();

        n.Degrade("config");

        Assert.Equal(1, primary.CancelCalls);
    }

    [Fact]
    public async Task Una_vez_degradado_no_vuelve_a_intentar_el_primario()
    {
        var (n, primary, fallback, _) = Build();
        primary.Throws = new HelperUnavailableException("ayudante caido");
        await n.PromptAsync(TimeSpan.FromSeconds(15), 5, default);

        await n.PromptAsync(TimeSpan.FromSeconds(15), 5, default);

        Assert.Equal(1, primary.PromptCalls);
        Assert.Equal(2, fallback.PromptCalls);
    }

    [Fact]
    public async Task Un_fallo_que_no_es_del_ayudante_no_degrada()
    {
        var (n, primary, fallback, _) = Build();
        primary.Throws = new InvalidOperationException("otra cosa");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => n.PromptAsync(TimeSpan.FromSeconds(15), 5, default));
        Assert.False(n.UsingFallback);
        Assert.Equal(0, fallback.PromptCalls);
    }

    [Fact]
    public async Task Cancel_y_Notify_siguen_a_la_implementacion_activa()
    {
        var (n, primary, fallback, _) = Build();

        await n.CancelAsync();
        await n.NotifyAsync("hola", default);
        Assert.Equal(1, primary.CancelCalls);
        Assert.Single(primary.Notifications);

        primary.Throws = new HelperUnavailableException("ayudante caido");
        await n.PromptAsync(TimeSpan.FromSeconds(15), 5, default);

        await n.CancelAsync();
        await n.NotifyAsync("adios", default);
        Assert.Equal(1, fallback.CancelCalls);
        Assert.Single(fallback.Notifications);
    }

    [Fact]
    public async Task Degradar_se_puede_forzar_desde_fuera()
    {
        var (n, primary, fallback, _) = Build();

        n.Degrade("助手连续启动失败");
        await n.PromptAsync(TimeSpan.FromSeconds(15), 5, default);

        Assert.True(n.UsingFallback);
        Assert.Equal(0, primary.PromptCalls);
        Assert.Equal(1, fallback.PromptCalls);
    }
}

public class WtsMessageNotifierTests
{
    [Theory]
    [InlineData(0.0, 1u)]
    [InlineData(-5.0, 1u)]
    [InlineData(0.4, 1u)]
    [InlineData(8.2, 9u)]
    [InlineData(15.0, 15u)]
    [InlineData(100000.0, 600u)]
    public void El_plazo_nunca_es_cero_ni_supera_el_maximo(double seconds, uint expected)
    {
        Assert.Equal(expected, WtsMessageNotifier.ClampTimeoutSeconds(TimeSpan.FromSeconds(seconds)));
    }
}

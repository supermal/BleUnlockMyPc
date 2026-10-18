using AutoUnlockService.Notifications;
using AutoUnlockService.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AutoUnlockService.Tests;

public class ResilientNotifierTests
{
    private sealed class FakeNotifier : INotifier
    {
        public PromptOutcome Outcome { get; set; } = PromptOutcome.Reject;
        public Exception? Throws { get; set; }
        public int PromptCalls { get; private set; }
        public double LastSnooze { get; private set; }
        public int CancelCalls { get; private set; }
        public List<string> Notifications { get; } = [];
        public Exception? NotifyThrows { get; set; }
        public Exception? CancelThrows { get; set; }

        public async Task<PromptOutcome> PromptAsync(double snoozeMinutes, CancellationToken ct)
        {
            PromptCalls++;
            LastSnooze = snoozeMinutes;
            if (Throws is not null)
            {
                await Task.Yield();
                throw Throws;
            }
            return Outcome;
        }

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

    private static (ResilientNotifier N, FakeNotifier Primary, FakeNotifier Fallback) Build()
    {
        var primary = new FakeNotifier();
        var fallback = new FakeNotifier();
        var n = new ResilientNotifier(primary, fallback, NullLogger<ResilientNotifier>.Instance);
        return (n, primary, fallback);
    }

    [Fact]
    public async Task Usa_el_primario_mientras_funcione()
    {
        var (n, primary, fallback) = Build();
        primary.Outcome = PromptOutcome.Snooze;

        var result = await n.PromptAsync(5, default);

        Assert.Equal(PromptOutcome.Snooze, result);
        Assert.Equal(1, primary.PromptCalls);
        Assert.Equal(0, fallback.PromptCalls);
        Assert.False(n.UsingFallback);
    }

    [Fact]
    public async Task Si_el_ayudante_cae_reintenta_en_el_acto_con_el_respaldo()
    {
        var (n, primary, fallback) = Build();
        primary.Throws = new HelperUnavailableException("ayudante caido");
        fallback.Outcome = PromptOutcome.Reject;

        var result = await n.PromptAsync(5, default);

        Assert.Equal(PromptOutcome.Reject, result);
        Assert.Equal(1, fallback.PromptCalls);
        Assert.True(n.UsingFallback);
    }

    [Fact]
    public async Task El_respaldo_recibe_el_mismo_aviso_sin_aritmetica_de_plazos()
    {
        // 判定改版：没有剩余期限可传递。降级只是换个通道把同一个提示再问一次
        var (n, primary, fallback) = Build();
        primary.Throws = new HelperUnavailableException("ayudante caido");

        await n.PromptAsync(7.5, default);

        Assert.Equal(1, fallback.PromptCalls);
        Assert.Equal(7.5, fallback.LastSnooze);
    }

    [Fact]
    public async Task Notify_con_ayudante_caido_degrada_y_reenvia_por_el_respaldo()
    {
        var (n, primary, fallback) = Build();
        primary.NotifyThrows = new HelperUnavailableException("ayudante caido");

        await n.NotifyAsync("suspendido", default);

        Assert.True(n.UsingFallback);
        Assert.Equal(["suspendido"], fallback.Notifications);
    }

    [Fact]
    public async Task Cancel_con_ayudante_caido_degrada_sin_lanzar()
    {
        var (n, primary, fallback) = Build();
        primary.CancelThrows = new HelperUnavailableException("ayudante caido");

        await n.CancelAsync();

        Assert.True(n.UsingFallback);
        Assert.Equal(1, fallback.CancelCalls);
    }

    [Fact]
    public async Task Una_cancelacion_no_degrada_de_forma_permanente()
    {
        var (n, primary, fallback) = Build();
        primary.Throws = new HelperUnavailableException("ayudante caido");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => n.PromptAsync(5, cts.Token));

        Assert.False(n.UsingFallback);
        Assert.Equal(0, fallback.PromptCalls);
    }

    [Fact]
    public void Degradar_desde_fuera_retira_lo_pendiente_del_primario()
    {
        var (n, primary, _) = Build();

        n.Degrade("config");

        Assert.Equal(1, primary.CancelCalls);
    }

    [Fact]
    public async Task Una_vez_degradado_no_vuelve_a_intentar_el_primario()
    {
        var (n, primary, fallback) = Build();
        primary.Throws = new HelperUnavailableException("ayudante caido");
        await n.PromptAsync(5, default);

        await n.PromptAsync(5, default);

        Assert.Equal(1, primary.PromptCalls);
        Assert.Equal(2, fallback.PromptCalls);
    }

    [Fact]
    public async Task Un_fallo_que_no_es_del_ayudante_no_degrada()
    {
        var (n, primary, fallback) = Build();
        primary.Throws = new InvalidOperationException("otra cosa");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => n.PromptAsync(5, default));
        Assert.False(n.UsingFallback);
        Assert.Equal(0, fallback.PromptCalls);
    }

    [Fact]
    public async Task Cancel_y_Notify_siguen_a_la_implementacion_activa()
    {
        var (n, primary, fallback) = Build();

        await n.CancelAsync();
        await n.NotifyAsync("hola", default);
        Assert.Equal(1, primary.CancelCalls);
        Assert.Single(primary.Notifications);

        primary.Throws = new HelperUnavailableException("ayudante caido");
        await n.PromptAsync(5, default);

        await n.CancelAsync();
        await n.NotifyAsync("adios", default);
        Assert.Equal(1, fallback.CancelCalls);
        Assert.Single(fallback.Notifications);
    }

    [Fact]
    public async Task Degradar_se_puede_forzar_desde_fuera()
    {
        var (n, primary, fallback) = Build();

        n.Degrade("助手连续启动失败");
        await n.PromptAsync(5, default);

        Assert.True(n.UsingFallback);
        Assert.Equal(0, primary.PromptCalls);
        Assert.Equal(1, fallback.PromptCalls);
    }

    // Primario que se queda esperando y, como HelperNotifier, termina cancelado cuando se le retira el aviso
    private sealed class BlockingPrimary : INotifier
    {
        private readonly TaskCompletionSource<PromptOutcome> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Started => _started.Task;

        public Task<PromptOutcome> PromptAsync(double snoozeMinutes, CancellationToken ct)
        {
            ct.Register(() => _tcs.TrySetCanceled(ct));
            _started.TrySetResult();
            return _tcs.Task;
        }

        public Task CancelAsync()
        {
            _tcs.TrySetCanceled();
            return Task.CompletedTask;
        }

        public Task NotifyAsync(string message, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task Degrade_externo_con_aviso_pendiente_pregunta_por_el_respaldo()
    {
        var primary = new BlockingPrimary();
        var fallback = new FakeNotifier { Outcome = PromptOutcome.Snooze };
        var n = new ResilientNotifier(primary, fallback, NullLogger<ResilientNotifier>.Instance);

        var prompt = n.PromptAsync(5, default);
        await primary.Started;
        n.Degrade("el lanzador se rindio");

        Assert.Equal(PromptOutcome.Snooze, await prompt.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(1, fallback.PromptCalls);
        Assert.True(n.UsingFallback);
    }

    [Fact]
    public async Task Cancelar_el_orquestador_un_aviso_pendiente_se_propaga_y_no_degrada()
    {
        var primary = new BlockingPrimary();
        var fallback = new FakeNotifier();
        var n = new ResilientNotifier(primary, fallback, NullLogger<ResilientNotifier>.Instance);

        var prompt = n.PromptAsync(5, default);
        await primary.Started;
        await n.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prompt.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(prompt.IsCanceled);
        Assert.False(n.UsingFallback);
        Assert.Equal(0, fallback.PromptCalls);
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

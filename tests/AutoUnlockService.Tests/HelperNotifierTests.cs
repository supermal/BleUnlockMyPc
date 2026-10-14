using AutoUnlockService.Notifications;
using AutoUnlockService.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AutoUnlockService.Tests;

// El contrato que importa: HelperUnavailableException solo cuando el usuario no ha visto nada
public class HelperNotifierTests
{
    private sealed class Rig : IAsyncDisposable
    {
        public string PipeName { get; } = TestSupport.UniquePipeName();
        public HelperPipeServer Server { get; }
        public HelperNotifier Notifier { get; }

        public Rig(TimeProvider? notifierTime = null)
        {
            Server = new HelperPipeServer(NullLogger<HelperPipeServer>.Instance, null, PipeName);
            Server.Start(CancellationToken.None);
            Notifier = new HelperNotifier(Server, NullLogger<HelperNotifier>.Instance, notifierTime);
        }

        public async Task<FakeHelperClient> ConnectHelperAsync()
        {
            var connected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            Server.Connected += s => connected.TrySetResult(s);
            var client = await FakeHelperClient.ConnectAsync(PipeName);
            await connected.Task.WaitAsync(TestSupport.Guard());
            return client;
        }

        public ValueTask DisposeAsync() => Server.DisposeAsync();
    }

    [Fact]
    public async Task Prompt_sin_ayudante_conectado_lanza_HelperUnavailable()
    {
        await using var rig = new Rig();
        await Assert.ThrowsAsync<HelperUnavailableException>(() =>
            rig.Notifier.PromptAsync(TimeSpan.FromSeconds(30), 5, CancellationToken.None));
    }

    [Fact]
    public async Task Notify_sin_ayudante_conectado_lanza_HelperUnavailable()
    {
        await using var rig = new Rig();
        await Assert.ThrowsAsync<HelperUnavailableException>(() =>
            rig.Notifier.NotifyAsync("hola", CancellationToken.None));
    }

    [Fact]
    public async Task Respuesta_real_del_ayudante_se_devuelve_sin_excepcion()
    {
        await using var rig = new Rig();
        await using var helper = await rig.ConnectHelperAsync();

        var prompt = rig.Notifier.PromptAsync(TimeSpan.FromSeconds(30), 5, CancellationToken.None);
        var command = Assert.IsType<HelperCommand.Prompt>(await helper.ReadCommandAsync(TestSupport.Guard()));
        await helper.SendAsync(HelperProtocol.BuildResponse(command.Id, PromptOutcome.Snooze));

        Assert.Equal(PromptOutcome.Snooze, await prompt.WaitAsync(TestSupport.Guard()));
    }

    [Fact]
    public async Task Respuesta_Timeout_del_ayudante_es_un_resultado_normal()
    {
        await using var rig = new Rig();
        await using var helper = await rig.ConnectHelperAsync();

        var prompt = rig.Notifier.PromptAsync(TimeSpan.FromSeconds(30), 5, CancellationToken.None);
        var command = Assert.IsType<HelperCommand.Prompt>(await helper.ReadCommandAsync(TestSupport.Guard()));
        await helper.SendAsync(HelperProtocol.BuildResponse(command.Id, PromptOutcome.Timeout));

        Assert.Equal(PromptOutcome.Timeout, await prompt.WaitAsync(TestSupport.Guard()));
    }

    [Fact]
    public async Task Respuesta_con_id_ajeno_se_ignora()
    {
        await using var rig = new Rig();
        await using var helper = await rig.ConnectHelperAsync();

        var prompt = rig.Notifier.PromptAsync(TimeSpan.FromSeconds(30), 5, CancellationToken.None);
        var command = Assert.IsType<HelperCommand.Prompt>(await helper.ReadCommandAsync(TestSupport.Guard()));
        await helper.SendAsync(HelperProtocol.BuildResponse(command.Id + 100, PromptOutcome.Reject));
        await helper.SendAsync(HelperProtocol.BuildResponse(command.Id, PromptOutcome.Dismissed));

        Assert.Equal(PromptOutcome.Dismissed, await prompt.WaitAsync(TestSupport.Guard()));
    }

    [Fact]
    public async Task Desconexion_con_aviso_pendiente_lanza_HelperUnavailable_enseguida()
    {
        await using var rig = new Rig();
        var helper = await rig.ConnectHelperAsync();

        // Plazo largo: si la excepcion dependiera del plazo la prueba se colgaria
        var prompt = rig.Notifier.PromptAsync(TimeSpan.FromMinutes(5), 5, CancellationToken.None);
        await helper.ReadCommandAsync(TestSupport.Guard());
        await helper.DisposeAsync();

        await Assert.ThrowsAsync<HelperUnavailableException>(() => prompt.WaitAsync(TestSupport.Guard()));
    }

    [Fact]
    public async Task Cancelacion_del_llamante_lanza_OperationCanceled_y_retira_el_Toast()
    {
        await using var rig = new Rig();
        await using var helper = await rig.ConnectHelperAsync();
        using var cts = new CancellationTokenSource();

        var prompt = rig.Notifier.PromptAsync(TimeSpan.FromSeconds(30), 5, cts.Token);
        var command = Assert.IsType<HelperCommand.Prompt>(await helper.ReadCommandAsync(TestSupport.Guard()));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prompt.WaitAsync(TestSupport.Guard()));
        var cancel = Assert.IsType<HelperCommand.Cancel>(await helper.ReadCommandAsync(TestSupport.Guard()));
        Assert.Equal(command.Id, cancel.Id);
    }

    [Fact]
    public async Task Plazos_fuera_de_rango_se_ajustan_a_los_topes_del_protocolo()
    {
        await using var rig = new Rig();
        await using var helper = await rig.ConnectHelperAsync();
        using var cts = new CancellationTokenSource();

        var prompt = rig.Notifier.PromptAsync(TimeSpan.FromHours(2), 99999, cts.Token);
        var command = Assert.IsType<HelperCommand.Prompt>(await helper.ReadCommandAsync(TestSupport.Guard()));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prompt.WaitAsync(TestSupport.Guard()));

        // Sin ajuste el ayudante descartaria la orden y el aviso no apareceria nunca
        Assert.Equal(HelperProtocol.MaxTimeoutSeconds, command.TimeoutSeconds);
        Assert.Equal(HelperProtocol.MaxSnoozeMinutes, command.SnoozeMinutes);
    }

    [Fact]
    public async Task Ayudante_que_no_responde_ni_con_margen_lanza_HelperUnavailable()
    {
        var time = new FakeTimeProvider();
        await using var rig = new Rig(time);
        await using var helper = await rig.ConnectHelperAsync();

        var prompt = rig.Notifier.PromptAsync(TimeSpan.FromSeconds(30), 5, CancellationToken.None);
        await helper.ReadCommandAsync(TestSupport.Guard());

        await TestSupport.AdvanceUntilAsync(time, () => prompt.IsCompleted);
        await Assert.ThrowsAsync<HelperUnavailableException>(() => prompt);
    }

    [Fact]
    public async Task Cancel_envia_CANCEL_del_aviso_pendiente()
    {
        await using var rig = new Rig();
        await using var helper = await rig.ConnectHelperAsync();
        using var cts = new CancellationTokenSource();

        var prompt = rig.Notifier.PromptAsync(TimeSpan.FromSeconds(30), 5, cts.Token);
        var command = Assert.IsType<HelperCommand.Prompt>(await helper.ReadCommandAsync(TestSupport.Guard()));
        await rig.Notifier.CancelAsync();

        var cancel = Assert.IsType<HelperCommand.Cancel>(await helper.ReadCommandAsync(TestSupport.Guard()));
        Assert.Equal(command.Id, cancel.Id);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prompt.WaitAsync(TestSupport.Guard()));
    }

    [Fact]
    public async Task Notify_entrega_el_mensaje_al_ayudante()
    {
        await using var rig = new Rig();
        await using var helper = await rig.ConnectHelperAsync();

        await rig.Notifier.NotifyAsync("自动解锁已暂停", CancellationToken.None);

        Assert.Equal(new HelperCommand.Notify("自动解锁已暂停"), await helper.ReadCommandAsync(TestSupport.Guard()));
    }

    private sealed class RecordingFallback : INotifier
    {
        public int PromptCalls { get; private set; }

        public Task<PromptOutcome> PromptAsync(TimeSpan timeout, double snoozeMinutes, CancellationToken ct)
        {
            PromptCalls++;
            return Task.FromResult(PromptOutcome.Reject);
        }

        public Task CancelAsync() => Task.CompletedTask;
        public Task NotifyAsync(string message, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task Con_ResilientNotifier_la_muerte_del_ayudante_pasa_al_dialogo_sin_esperar_al_plazo()
    {
        await using var rig = new Rig();
        var helper = await rig.ConnectHelperAsync();
        var fallback = new RecordingFallback();
        var resilient = new ResilientNotifier(
            rig.Notifier, fallback, NullLogger<ResilientNotifier>.Instance, TimeProvider.System);

        var prompt = resilient.PromptAsync(TimeSpan.FromMinutes(5), 5, CancellationToken.None);
        await helper.ReadCommandAsync(TestSupport.Guard());
        await helper.DisposeAsync();

        Assert.Equal(PromptOutcome.Reject, await prompt.WaitAsync(TestSupport.Guard()));
        Assert.Equal(1, fallback.PromptCalls);
        Assert.True(resilient.UsingFallback);
    }
}

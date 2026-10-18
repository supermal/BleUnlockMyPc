using AutoUnlockService.Notifications;
using AutoUnlockService.Session;
using Microsoft.Extensions.Logging.Abstractions;
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
        private readonly TestGuard _guard = new();
        public CancellationToken Guard => _guard.Token;

        public Rig()
        {
            Server = new HelperPipeServer(NullLogger<HelperPipeServer>.Instance, null, PipeName);
            Server.Start(CancellationToken.None);
            Notifier = new HelperNotifier(Server, NullLogger<HelperNotifier>.Instance);
        }

        public async Task<FakeHelperClient> ConnectHelperAsync()
        {
            var connected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            Server.Connected += s => connected.TrySetResult(s);
            var client = await FakeHelperClient.ConnectAsync(PipeName);
            await connected.Task.WaitAsync(Guard);
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            _guard.Dispose();
        }
    }

    // Inspecciona el estado de la tarea en vez de esperarla con el guardian: si el guardian vence,
    // la tarea sigue pendiente y la asercion falla por la razon correcta (un OperationCanceled del
    // propio guardian seria indistinguible del que se quiere comprobar)
    private static async Task AssertCanceledAsync(Task prompt, CancellationToken guard)
    {
        await Task.WhenAny(prompt, Task.Delay(Timeout.Infinite, guard));
        Assert.True(prompt.IsCompleted, "El Prompt sigue pendiente (colgado)");
        Assert.True(prompt.IsCanceled, $"El Prompt debia terminar cancelado, estado: {prompt.Status}");
    }

    [Fact]
    public async Task Prompt_sin_ayudante_conectado_lanza_HelperUnavailable()
    {
        await using var rig = new Rig();
        await Assert.ThrowsAsync<HelperUnavailableException>(() =>
            rig.Notifier.PromptAsync(5, CancellationToken.None));
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

        var prompt = rig.Notifier.PromptAsync(5, CancellationToken.None);
        var command = Assert.IsType<HelperCommand.Prompt>(await helper.ReadCommandAsync(rig.Guard));
        await helper.SendAsync(HelperProtocol.BuildResponse(command.Id, PromptOutcome.Snooze));

        Assert.Equal(PromptOutcome.Snooze, await prompt.WaitAsync(rig.Guard));
    }

    [Fact]
    public async Task Respuesta_Timeout_del_ayudante_es_un_resultado_normal()
    {
        await using var rig = new Rig();
        await using var helper = await rig.ConnectHelperAsync();

        var prompt = rig.Notifier.PromptAsync(5, CancellationToken.None);
        var command = Assert.IsType<HelperCommand.Prompt>(await helper.ReadCommandAsync(rig.Guard));
        await helper.SendAsync(HelperProtocol.BuildResponse(command.Id, PromptOutcome.Timeout));

        Assert.Equal(PromptOutcome.Timeout, await prompt.WaitAsync(rig.Guard));
    }

    [Fact]
    public async Task Respuesta_con_id_ajeno_se_ignora()
    {
        await using var rig = new Rig();
        await using var helper = await rig.ConnectHelperAsync();

        var prompt = rig.Notifier.PromptAsync(5, CancellationToken.None);
        var command = Assert.IsType<HelperCommand.Prompt>(await helper.ReadCommandAsync(rig.Guard));
        await helper.SendAsync(HelperProtocol.BuildResponse(command.Id + 100, PromptOutcome.Reject));
        await helper.SendAsync(HelperProtocol.BuildResponse(command.Id, PromptOutcome.Dismissed));

        Assert.Equal(PromptOutcome.Dismissed, await prompt.WaitAsync(rig.Guard));
    }

    [Fact]
    public async Task Desconexion_con_aviso_pendiente_lanza_HelperUnavailable_enseguida()
    {
        await using var rig = new Rig();
        var helper = await rig.ConnectHelperAsync();

        // 没有期限：断开是唯一的“用户没看到”信号，必须立即降级
        var prompt = rig.Notifier.PromptAsync(5, CancellationToken.None);
        await helper.ReadCommandAsync(rig.Guard);
        await helper.DisposeAsync();

        await Assert.ThrowsAsync<HelperUnavailableException>(() => prompt.WaitAsync(rig.Guard));
    }

    [Fact]
    public async Task Ayudante_mudo_mantiene_la_espera_hasta_la_desconexion()
    {
        await using var rig = new Rig();
        var helper = await rig.ConnectHelperAsync();

        // 判定改版：没有自动期限。助手不回话，等待就一直在；断开才降级
        var prompt = rig.Notifier.PromptAsync(5, CancellationToken.None);
        await helper.ReadCommandAsync(rig.Guard);
        await Task.Delay(100);
        Assert.False(prompt.IsCompleted);

        await helper.DisposeAsync();
        await Assert.ThrowsAsync<HelperUnavailableException>(() => prompt.WaitAsync(rig.Guard));
    }

    [Fact]
    public async Task Cancelacion_del_llamante_lanza_OperationCanceled_y_retira_el_Toast()
    {
        await using var rig = new Rig();
        await using var helper = await rig.ConnectHelperAsync();
        using var cts = new CancellationTokenSource();

        var prompt = rig.Notifier.PromptAsync(5, cts.Token);
        var command = Assert.IsType<HelperCommand.Prompt>(await helper.ReadCommandAsync(rig.Guard));
        cts.Cancel();

        await AssertCanceledAsync(prompt, rig.Guard);
        var cancel = Assert.IsType<HelperCommand.Cancel>(await helper.ReadCommandAsync(rig.Guard));
        Assert.Equal(command.Id, cancel.Id);
    }

    [Fact]
    public async Task Snooze_fuera_de_rango_se_ajusta_al_tope_del_protocolo()
    {
        await using var rig = new Rig();
        await using var helper = await rig.ConnectHelperAsync();
        using var cts = new CancellationTokenSource();

        var prompt = rig.Notifier.PromptAsync(99999, cts.Token);
        var command = Assert.IsType<HelperCommand.Prompt>(await helper.ReadCommandAsync(rig.Guard));
        cts.Cancel();
        await AssertCanceledAsync(prompt, rig.Guard);

        // Sin ajuste el ayudante descartaria la orden y el aviso no apareceria nunca
        Assert.Equal(HelperProtocol.MaxSnoozeMinutes, command.SnoozeMinutes);
    }

    [Fact]
    public async Task CancelAsync_termina_el_Prompt_en_curso_con_OperationCanceled_y_envia_CANCEL()
    {
        await using var rig = new Rig();
        await using var helper = await rig.ConnectHelperAsync();

        // 没有期限：CancelAsync es la única salida normal que llega desde el orquestador
        var prompt = rig.Notifier.PromptAsync(5, CancellationToken.None);
        var command = Assert.IsType<HelperCommand.Prompt>(await helper.ReadCommandAsync(rig.Guard));
        await rig.Notifier.CancelAsync();

        // Destino del Prompt: cancelado, nunca HelperUnavailable (no es una averia)
        await AssertCanceledAsync(prompt, rig.Guard);
        var cancel = Assert.IsType<HelperCommand.Cancel>(await helper.ReadCommandAsync(rig.Guard));
        Assert.Equal(command.Id, cancel.Id);
    }

    [Fact]
    public async Task Cancel_durante_un_aviso_no_deja_degradado_a_ResilientNotifier()
    {
        await using var rig = new Rig();
        await using var helper = await rig.ConnectHelperAsync();
        var fallback = new RecordingFallback();
        var resilient = new ResilientNotifier(
            rig.Notifier, fallback, NullLogger<ResilientNotifier>.Instance);

        var prompt = resilient.PromptAsync(5, CancellationToken.None);
        await helper.ReadCommandAsync(rig.Guard);
        await resilient.CancelAsync();
        await AssertCanceledAsync(prompt, rig.Guard);

        Assert.False(resilient.UsingFallback);
        Assert.Equal(0, fallback.PromptCalls);

        // El siguiente aviso sigue yendo por el ayudante
        await helper.ReadCommandAsync(rig.Guard);   // CANCEL del anterior
        var next = resilient.PromptAsync(5, CancellationToken.None);
        Assert.IsType<HelperCommand.Prompt>(await helper.ReadCommandAsync(rig.Guard));
        await resilient.CancelAsync();
        await AssertCanceledAsync(next, rig.Guard);
        Assert.False(resilient.UsingFallback);
    }

    [Fact]
    public async Task Notify_entrega_el_mensaje_al_ayudante()
    {
        await using var rig = new Rig();
        await using var helper = await rig.ConnectHelperAsync();

        await rig.Notifier.NotifyAsync("自动解锁已暂停", CancellationToken.None);

        Assert.Equal(new HelperCommand.Notify("自动解锁已暂停"), await helper.ReadCommandAsync(rig.Guard));
    }

    private sealed class RecordingFallback : INotifier
    {
        public int PromptCalls { get; private set; }

        public Task<PromptOutcome> PromptAsync(double snoozeMinutes, CancellationToken ct)
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
            rig.Notifier, fallback, NullLogger<ResilientNotifier>.Instance);

        var prompt = resilient.PromptAsync(5, CancellationToken.None);
        await helper.ReadCommandAsync(rig.Guard);
        await helper.DisposeAsync();

        Assert.Equal(PromptOutcome.Reject, await prompt.WaitAsync(rig.Guard));
        Assert.Equal(1, fallback.PromptCalls);
        Assert.True(resilient.UsingFallback);
    }

    [Fact]
    public async Task Degrade_externo_con_aviso_pendiente_pregunta_por_el_respaldo_y_retira_el_Toast()
    {
        await using var rig = new Rig();
        await using var helper = await rig.ConnectHelperAsync();
        var fallback = new RecordingFallback();
        var resilient = new ResilientNotifier(
            rig.Notifier, fallback, NullLogger<ResilientNotifier>.Instance);

        var prompt = resilient.PromptAsync(5, CancellationToken.None);
        await helper.ReadCommandAsync(rig.Guard);
        resilient.Degrade("el lanzador se rindio");

        Assert.Equal(PromptOutcome.Reject, await prompt.WaitAsync(rig.Guard));
        Assert.Equal(1, fallback.PromptCalls);
        Assert.IsType<HelperCommand.Cancel>(await helper.ReadCommandAsync(rig.Guard));
    }
}

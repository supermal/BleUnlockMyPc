using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using AutoUnlockService.Session;

namespace AutoUnlockHelper;

internal static class Program
{
    private const string PipeName = "AutoUnlockHelper";

    // Plazo maximo para que el servicio acepte una escritura; pasado esto, el canal se da por muerto
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    // Los eventos del Toast y el temporizador solo encolan: nunca bloquean sobre el pipe
    private static readonly Channel<string> Outbox =
        Channel.CreateBounded<string>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait });

    // Se cancela cuando el canal esta muerto; el bucle de lectura sale y el servicio relanza el ayudante
    private static readonly CancellationTokenSource Dead = new();

    private static async Task<int> Main()
    {
        using var prompt = new ToastPrompt();
        prompt.Responded += (id, outcome) => Send(HelperProtocol.BuildResponse(id, outcome));

        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(30_000);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            return 1;   // el servicio no esta escuchando: se sale y se relanzara
        }

        var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = false };
        var writerTask = Task.Run(() => WriteLoopAsync(writer));
        Send(HelperProtocol.BuildHello(Process.GetCurrentProcess().SessionId));

        using var reader = new StreamReader(pipe, Encoding.UTF8);
        var exitCode = 0;
        try
        {
            while (await reader.ReadLineAsync(Dead.Token) is { } line)
            {
                if (!HelperProtocol.TryParseCommand(line, out var command))
                {
                    // Descartar en silencio seria indistinguible de un fallo: se deja constancia
                    Log($"Comando descartado (mal formado o fuera de rango): {Truncate(line)}");
                    continue;
                }

                // Un comando que falla no puede matar el proceso: se registra y se sigue sirviendo
                try
                {
                    Handle(command, prompt);
                }
                catch (Exception ex)
                {
                    Log($"Fallo al ejecutar {command.GetType().Name}: {ex}");
                }
            }
        }
        catch (IOException)
        {
            // El canal se rompio de golpe: se trata igual que un cierre limpio
        }
        catch (OperationCanceledException)
        {
            exitCode = 2;   // el canal de escritura murio
        }

        // El servicio cerro el canal (o murio): se retira el Toast pendiente (Dispose) y se sale,
        // para que el servicio pueda relanzar el ayudante
        Outbox.Writer.TryComplete();
        await Task.WhenAny(writerTask, Task.Delay(WriteTimeout));
        return exitCode;
    }

    private static void Handle(HelperCommand command, ToastPrompt prompt)
    {
        switch (command)
        {
            case HelperCommand.Prompt p:
                prompt.Show(p.Id, p.TimeoutSeconds, p.SnoozeMinutes);
                break;

            case HelperCommand.Cancel c:
                prompt.Cancel(c.Id);
                break;

            case HelperCommand.Lock:
                // LOCKED solo si Windows acepto la orden
                if (ScreenLock.Lock()) Send(HelperProtocol.BuildLocked());
                break;

            case HelperCommand.Notify n:
                prompt.Notify(n.Message);
                break;
        }
    }

    // No bloquea nunca: si la cola esta llena el servicio no lee, y eso es un canal muerto
    private static void Send(string line)
    {
        if (!Outbox.Writer.TryWrite(line)) Dead.Cancel();
    }

    private static async Task WriteLoopAsync(StreamWriter writer)
    {
        try
        {
            await foreach (var line in Outbox.Reader.ReadAllAsync())
            {
                using var cts = new CancellationTokenSource(WriteTimeout);
                await writer.WriteAsync(line.AsMemory(), cts.Token);
                await writer.FlushAsync(cts.Token);
            }
        }
        catch (Exception ex)
        {
            // Cualquier fallo del escritor (atasco, canal roto, error inesperado) es canal muerto:
            // el bucle de lectura saldra y el servicio relanzara el ayudante
            Log($"Canal de escritura muerto: {ex}");
            Dead.Cancel();
        }
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "...";

    // Registro minimo en LOCALAPPDATA; el ayudante nunca maneja credenciales
    private static void Log(string message)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoUnlock");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "helper.log"),
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Registrar es de mejor esfuerzo
        }
    }
}

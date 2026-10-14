using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using AutoUnlockService.Session;

namespace AutoUnlockHelper;

internal static class Program
{
    private const string PipeName = "AutoUnlockHelper";

    private static readonly object WriteLock = new();
    private static StreamWriter? _writer;

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

        _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
        Send(HelperProtocol.BuildHello(Process.GetCurrentProcess().SessionId));

        using var reader = new StreamReader(pipe, Encoding.UTF8);
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (!HelperProtocol.TryParseCommand(line, out var command)) continue;

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
        }
        catch (IOException)
        {
            // El canal se rompio de golpe: se trata igual que un cierre limpio
        }

        // El servicio cerro el canal: se retira el Toast pendiente (Dispose) y se sale,
        // para que el servicio pueda relanzar el ayudante
        return 0;
    }

    // Varios hilos escriben (bucle de lectura, eventos del Toast, temporizador): se serializa
    private static void Send(string line)
    {
        lock (WriteLock)
        {
            try { _writer?.Write(line); }
            catch (IOException) { /* el canal murio; el servicio aplicara su propio plazo */ }
            catch (ObjectDisposedException) { }
        }
    }
}

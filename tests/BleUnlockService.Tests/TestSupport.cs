using Microsoft.Extensions.Time.Testing;

namespace BleUnlockService.Tests;

internal static class TestSupport
{
    // Avanza el reloj simulado en pasos hasta que se cumple la condicion; no duerme tiempo real.
    // El presupuesto de pasos es holgado porque el tiempo real que necesita el otro hilo varia con la carga
    public static async Task AdvanceUntilAsync(FakeTimeProvider time, Func<bool> done, int maxSteps = 2_000_000)
    {
        for (int i = 0; i < maxSteps; i++)
        {
            if (done()) return;
            time.Advance(TimeSpan.FromSeconds(1));
            // Ceder el hilo de verdad: el codigo bajo prueba corre en otro hilo y bajo carga necesita turno
            Thread.Yield();
            await Task.Yield();
        }
        throw new TimeoutException("La condicion no se cumplio avanzando el reloj simulado");
    }

    public static string UniquePipeName() => $"BleUnlockTest-{Guid.NewGuid():N}";
}

// Plazo de seguridad real por prueba: solo se agota si el codigo bajo prueba se cuelga.
// Cada prueba tiene el suyo y lo libera al terminar, asi un cuelgue no contamina a las demas
internal sealed class TestGuard(int seconds = 30) : IDisposable
{
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(seconds));
    public CancellationToken Token => _cts.Token;
    public void Dispose() => _cts.Dispose();
}

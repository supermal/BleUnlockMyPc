using Microsoft.Extensions.Time.Testing;

namespace AutoUnlockService.Tests;

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

    public static string UniquePipeName() => $"AutoUnlockTest-{Guid.NewGuid():N}";

    // Plazo de seguridad real compartido (una sola fuente, sin fuga por llamada): solo se agota si algo se cuelga
    private static readonly CancellationTokenSource GuardSource = new(TimeSpan.FromSeconds(120));

    public static CancellationToken Guard() => GuardSource.Token;
}

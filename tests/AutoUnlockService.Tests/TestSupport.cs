using Microsoft.Extensions.Time.Testing;

namespace AutoUnlockService.Tests;

internal static class TestSupport
{
    // Avanza el reloj simulado en pasos hasta que se cumple la condicion; no duerme tiempo real
    public static async Task AdvanceUntilAsync(FakeTimeProvider time, Func<bool> done, int maxSteps = 50_000)
    {
        for (int i = 0; i < maxSteps; i++)
        {
            if (done()) return;
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }
        throw new TimeoutException("La condicion no se cumplio avanzando el reloj simulado");
    }

    public static string UniquePipeName() => $"AutoUnlockTest-{Guid.NewGuid():N}";

    // Plazo de seguridad real: solo se agota si el codigo bajo prueba se cuelga
    public static CancellationToken Guard(int seconds = 15) => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;
}

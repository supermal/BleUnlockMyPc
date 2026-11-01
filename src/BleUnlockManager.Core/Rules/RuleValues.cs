using System.Globalization;
using System.Text.Json;
using BleUnlockManager.Core.Config;

namespace BleUnlockManager.Core.Rules;

/// <summary>
/// Los diez valores configurables que aparecen en la página de reglas, tal como los usa el servicio.
/// </summary>
public sealed record RuleValues(
    double LockDbm,
    double UnlockDbm,
    int PromptCount,
    int ConfirmCount,
    int UnlockCount,
    int SampleIntervalMs,
    double GraceSeconds,
    double UnlockTimeoutSeconds,
    double RejectSnoozeSeconds,
    double SnoozeMinutes)
{
    /// <summary>Los mismos valores que una configuración recién creada (y que el servicio).</summary>
    public static RuleValues Defaults { get; } = FromConfig(new BleUnlockConfig());

    public static RuleValues FromConfig(BleUnlockConfig config) => new(
        config.Proximity.LockThresholdDbm,
        config.Proximity.UnlockThresholdDbm,
        config.Proximity.LockPromptSampleCount,
        config.Proximity.LockConfirmSampleCount,
        config.Proximity.UnlockSampleCount,
        config.Proximity.SampleIntervalMs,
        config.Proximity.StartupGraceSeconds,
        config.Proximity.UnlockPendingTimeoutSeconds,
        config.Notification.RejectSnoozeSeconds,
        config.Notification.SnoozeMinutes);

    /// <summary>
    /// Lee la configuración del servicio. Si no existe o no se puede leer, devuelve los valores
    /// predeterminados y <paramref name="fromConfig"/> = false; nunca lanza.
    /// </summary>
    public static RuleValues Load(string configPath, out bool fromConfig)
    {
        try
        {
            var values = FromConfig(ConfigDocument.Load(configPath));
            fromConfig = true;
            return values;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            fromConfig = false;
            return Defaults;
        }
    }

    internal static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>Lecturas de ejemplo para la sección 8, derivadas de los umbrales vigentes.</summary>
public sealed record RuleExamples(int Trigger, int Back, IReadOnlyList<int> Withdraw)
{
    // Con los valores predeterminados (-80 / -45, separación 35) dan -70, -72, -68 y -65
    private static readonly int[] WithdrawOffsets = [10, 8, 12, 15];
    private const double ReferenceSpan = 35;

    public static RuleExamples From(RuleValues values)
    {
        var lockDbm = (int)Math.Round(values.LockDbm, MidpointRounding.AwayFromZero);
        var unlockDbm = (int)Math.Round(values.UnlockDbm, MidpointRounding.AwayFromZero);
        var span = Math.Max(1, unlockDbm - lockDbm);

        // Siempre por encima del umbral de bloqueo y sin pasar del de desbloqueo
        var withdraw = WithdrawOffsets
            .Select(o => lockDbm + (int)Math.Round(span * o / ReferenceSpan, MidpointRounding.AwayFromZero))
            .Select(w => Math.Clamp(w, lockDbm + 1, Math.Max(lockDbm + 1, unlockDbm)))
            .ToArray();

        return new RuleExamples(lockDbm - 2, unlockDbm + 5, withdraw);
    }

    // RuleExamples se compara por valor también en la lista
    public bool Equals(RuleExamples? other) =>
        other is not null && Trigger == other.Trigger && Back == other.Back && Withdraw.SequenceEqual(other.Withdraw);

    public override int GetHashCode() => HashCode.Combine(Trigger, Back, Withdraw.Count);
}

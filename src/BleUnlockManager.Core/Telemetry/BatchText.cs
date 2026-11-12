using System.Globalization;
using BleUnlockManager.Core.Management;
using BleUnlockManager.Core.Resources;

namespace BleUnlockManager.Core.Telemetry;

/// <summary>Progreso de un lote como texto: «3/5» (lecturas) o «12/30 秒» (observación de un cambio brusco).</summary>
public static class BatchText
{
    public static bool IsSeconds(SnapshotBatch batch) => batch.Kind is "suddenDrop" or "suddenRise";

    public static string Progress(SnapshotBatch batch)
    {
        var count = batch.N.ToString(CultureInfo.InvariantCulture);
        return IsSeconds(batch)
            ? $"{count}/{string.Format(CultureInfo.CurrentCulture, CoreStrings.Batch_OfSeconds, batch.Of)}"
            : $"{count}/{batch.Of.ToString(CultureInfo.InvariantCulture)}";
    }
}

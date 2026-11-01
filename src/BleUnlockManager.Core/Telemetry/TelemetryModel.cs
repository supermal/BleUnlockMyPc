using BleUnlockManager.Core.Management;

namespace BleUnlockManager.Core.Telemetry;

/// <summary>
/// Estado de telemetría de toda la aplicación, sin dependencias de interfaz: aplica las tramas del
/// canal de gestión y deriva señal actual, zona, lote y estado de la bandeja. Un solo hilo (el de la UI).
/// </summary>
public sealed class TelemetryModel
{
    private long _lastGen = -1;
    private bool _reconnected;

    public SampleHistory History { get; } = new();

    public bool Connected { get; private set; }

    /// <summary>Última trama de datos; null tras un saludo o una desconexión.</summary>
    public SnapshotFrame? Snapshot { get; private set; }

    /// <summary>Estado del servicio según el SCM (null = desconocido).</summary>
    public bool? ServiceRunning { get; set; }

    public double? CurrentDbm => Connected && Snapshot is not null ? History.Latest?.Rssi : null;

    public SignalZone? Zone => CurrentDbm is { } dbm && Snapshot?.Thr is { } thr
        ? SignalZones.Classify(dbm, thr.LockDbm, thr.UnlockDbm)
        : null;

    public SnapshotBatch? Batch => Connected ? Snapshot?.Batch : null;

    public string? StateName => Connected ? Snapshot?.State?.Name : null;

    public TrayState TrayState => TrayStates.Resolve(ServiceRunning, Connected, StateName, Batch);

    /// <summary>Aplica una trama; devuelve true si el histórico se vació (la curva debe redibujarse entera).</summary>
    public bool Apply(ManagementFrame frame)
    {
        switch (frame)
        {
            case HelloFrame:
                // Sesión nueva: la primera trama trae de nuevo los 5 minutos de retrospectiva
                History.Clear();
                Snapshot = null;
                _lastGen = -1;
                return true;

            case SnapshotFrame snapshot:
                MarkConnected(true);
                var reset = snapshot.Gen != _lastGen;
                if (reset)
                {
                    History.Clear();
                    _lastGen = snapshot.Gen;
                }

                History.AddRange(snapshot.Samples);
                History.Prune(snapshot.TsMs);
                Snapshot = snapshot;
                return reset;

            default:
                return false;
        }
    }

    public void SetConnected(bool connected)
    {
        MarkConnected(connected);
        if (!connected)
        {
            // La curva se conserva hasta el próximo saludo; el estado en vivo deja de ser válido
            Snapshot = null;
        }
    }

    /// <summary>
    /// true una sola vez tras pasar de desconectado a conectado. Al iniciar sesión el gestor arranca
    /// antes que el servicio (inicio automático retrasado): lo que depende del servicio, como el nombre
    /// del dispositivo, debe volver a leerse cuando el canal se conecta.
    /// </summary>
    public bool TakeReconnected()
    {
        var value = _reconnected;
        _reconnected = false;
        return value;
    }

    private void MarkConnected(bool connected)
    {
        if (connected && !Connected)
        {
            _reconnected = true;
        }

        Connected = connected;
    }

    public TrayTooltip Tooltip(string deviceName) => TrayStates.Tooltip(
        TrayState,
        StateName,
        deviceName,
        CurrentDbm,
        Snapshot?.Thr?.LockDbm,
        Snapshot is { } s ? TelemetryFormat.LastMatch(s.LastMatchMs) : null,
        Batch);
}

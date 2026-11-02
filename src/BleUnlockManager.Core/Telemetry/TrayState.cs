using System.Globalization;
using BleUnlockManager.Core.Config;
using BleUnlockManager.Core.Management;
using BleUnlockManager.Core.Resources;

namespace BleUnlockManager.Core.Telemetry;

/// <summary>Estado que muestra el icono de la bandeja (forma y color del distintivo).</summary>
public enum TrayState
{
    Running,
    Leaving,
    Suspended,
    Stopped,
    Connecting,
    /// <summary>Servicio en marcha pero sin terminar de configurar (sin dispositivo o sin credenciales).</summary>
    NeedsSetup,
}

/// <summary>Texto emergente de la bandeja en dos líneas: estado y dispositivo.</summary>
public sealed record TrayTooltip(string Line1, string Line2)
{
    // Límite de Windows para NOTIFYICONDATA.szTip (128 incluido el terminador)
    public const int MaxLength = 127;

    public override string ToString()
    {
        var text = Line1 + "\n" + Line2;
        return text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";
    }
}

public static class TrayStates
{
    private static string Prefix => CoreStrings.Tray_Prefix;

    public static TrayState Resolve(bool? serviceRunning, bool connected, string? stateName, SnapshotBatch? batch,
        SetupState setup = SetupState.Complete)
    {
        if (serviceRunning == false)
        {
            return TrayState.Stopped;
        }

        if (!connected)
        {
            return TrayState.Connecting;
        }

        // Sin dispositivo no se vigila nada: no hay otro estado que mostrar
        if (setup == SetupState.NoDevice)
        {
            return TrayState.NeedsSetup;
        }

        if (stateName == "Suspended")
        {
            return TrayState.Suspended;
        }

        // Un lote de llegada (unlock) no es una salida: el usuario está volviendo
        var leaving = stateName == "PendingLock" || batch is { Kind: "leave" or "lockConfirm" };
        if (leaving)
        {
            return TrayState.Leaving;
        }

        // Sin credenciales el bloqueo funciona; lo que falta (el desbloqueo) se recuerda en reposo
        return setup == SetupState.NoCredentials ? TrayState.NeedsSetup : TrayState.Running;
    }

    public static TrayTooltip Tooltip(
        TrayState state,
        string? stateName,
        string deviceName,
        double? dbm,
        double? lockDbm,
        string? lastMatchText,
        SnapshotBatch? batch,
        SetupState setup = SetupState.Complete) => state switch
        {
            TrayState.NeedsSetup => new(
                Prefix + CoreStrings.Tray_NeedsSetupTitle,
                setup == SetupState.NoCredentials ? CoreStrings.Tray_NoCredentialsHint : CoreStrings.Tray_NoDeviceHint),
            TrayState.Stopped => new(Prefix + CoreStrings.Tray_StoppedTitle, CoreStrings.Tray_StoppedHint),
            TrayState.Connecting => new(Prefix + CoreStrings.Tray_ConnectingTitle, CoreStrings.Tray_ConnectingHint),
            TrayState.Suspended => new(Prefix + CoreStrings.Tray_SuspendedTitle, CoreStrings.Tray_SuspendedHint),
            TrayState.Leaving => new(
                Prefix + LeavingTitle(batch),
                lockDbm is { } l
                    ? string.Format(CoreStrings.Tray_BelowLockThreshold, Signal(deviceName, dbm), Whole(l))
                    : Signal(deviceName, dbm)),
            _ => new(
                Prefix + TelemetryFormat.StateName(stateName),
                lastMatchText is { Length: > 0 }
                    ? string.Format(CoreStrings.Tray_LastMatch, Signal(deviceName, dbm), lastMatchText)
                    : Signal(deviceName, dbm)),
        };

    private static string LeavingTitle(SnapshotBatch? batch) => batch switch
    {
        { Kind: "lockConfirm" } b => string.Format(CoreStrings.Tray_LockConfirm, b.N, b.Of),
        { Kind: "leave" } b => string.Format(CoreStrings.Tray_LeaveConfirm, b.N, b.Of),
        _ => TelemetryFormat.StateName("PendingLock"),
    };

    private static string Signal(string deviceName, double? dbm) =>
        dbm is { } v ? string.Format(CoreStrings.Tray_SignalDbm, deviceName, Whole(v)) : string.Format(CoreStrings.Tray_NoSignal, deviceName);

    private static string Whole(double value) =>
        Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
}

using System.Globalization;
using AutoUnlockManager.Core.Management;

namespace AutoUnlockManager.Core.Telemetry;

/// <summary>Estado que muestra el icono de la bandeja (forma y color del distintivo).</summary>
public enum TrayState
{
    Running,
    Leaving,
    Suspended,
    Stopped,
    Connecting,
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
    private const string Prefix = "AutoUnlock：";

    public static TrayState Resolve(bool? serviceRunning, bool connected, string? stateName, SnapshotBatch? batch)
    {
        if (serviceRunning == false)
        {
            return TrayState.Stopped;
        }

        if (!connected)
        {
            return TrayState.Connecting;
        }

        if (stateName == "Suspended")
        {
            return TrayState.Suspended;
        }

        // Un lote de llegada (unlock) no es una salida: el usuario está volviendo
        var leaving = stateName == "PendingLock" || batch is { Kind: "leave" or "lockConfirm" };
        return leaving ? TrayState.Leaving : TrayState.Running;
    }

    public static TrayTooltip Tooltip(
        TrayState state,
        string? stateName,
        string deviceName,
        double? dbm,
        double? lockDbm,
        string? lastMatchText,
        SnapshotBatch? batch) => state switch
        {
            TrayState.Stopped => new(Prefix + "服务已停止", "右键选择「启动服务」即可恢复"),
            TrayState.Connecting => new(Prefix + "正在连接服务…", "服务启动后会自动连接"),
            TrayState.Suspended => new(Prefix + "自动解锁已暂停，请检查密码", "在「配置」页更新密码后自动恢复"),
            TrayState.Leaving => new(
                Prefix + LeavingTitle(batch),
                lockDbm is { } l
                    ? $"{Signal(deviceName, dbm)}，低于锁定阈值 {Whole(l)}"
                    : Signal(deviceName, dbm)),
            _ => new(
                Prefix + TelemetryFormat.StateName(stateName),
                lastMatchText is { Length: > 0 }
                    ? $"{Signal(deviceName, dbm)}，最近匹配 {lastMatchText}"
                    : Signal(deviceName, dbm)),
        };

    private static string LeavingTitle(SnapshotBatch? batch) => batch switch
    {
        { Kind: "lockConfirm" } b => $"锁屏确认 {b.N}/{b.Of}",
        { Kind: "leave" } b => $"离开确认 {b.N}/{b.Of}",
        _ => TelemetryFormat.StateName("PendingLock"),
    };

    private static string Signal(string deviceName, double? dbm) =>
        dbm is { } v ? $"{deviceName} {Whole(v)} dBm" : $"{deviceName}，暂无信号";

    private static string Whole(double value) =>
        Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
}

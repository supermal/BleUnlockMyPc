namespace BleUnlockManager.Core.Logs;

/// <summary>
/// Recuento por nivel para los botones de filtro. Cuenta registros, no líneas: las líneas de
/// continuación (pilas de excepción) y las no reconocidas no suman; Fatal cuenta como error.
/// </summary>
public sealed record LogLevelCounts(int Debug, int Info, int Warning, int Error)
{
    public static readonly LogLevelCounts Empty = new(0, 0, 0, 0);

    public static LogLevelCounts From(IEnumerable<LogEntry> entries) =>
        entries.Aggregate(Empty, (counts, entry) => counts.Add(entry));

    public LogLevelCounts Add(LogEntry entry)
    {
        if (entry.IsContinuation)
        {
            return this;
        }

        return entry.Severity switch
        {
            LogSeverity.Debug => this with { Debug = Debug + 1 },
            LogSeverity.Information => this with { Info = Info + 1 },
            LogSeverity.Warning => this with { Warning = Warning + 1 },
            LogSeverity.Error or LogSeverity.Fatal => this with { Error = Error + 1 },
            _ => this,
        };
    }
}

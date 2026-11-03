using System.Globalization;
using BleUnlockManager.Core.Resources;

namespace BleUnlockManager.Core.Logs;

/// <summary>Errores y advertencias escritos después de la última vez que se abrió la página «日志».</summary>
public readonly record struct LogUnreadCounts(int Errors, int Warnings)
{
    public static readonly LogUnreadCounts None = new(0, 0);

    public bool IsEmpty => Errors == 0 && Warnings == 0;
}

/// <summary>Contador junto a «日志»: errores en rojo si hay alguno; si no, advertencias en amarillo.</summary>
/// <param name="Tone">«danger», «warning» o vacío (sin contador).</param>
public sealed record LogUnreadBadge(string Text, string Tone, string ToolTip)
{
    public static readonly LogUnreadBadge Hidden = new("", "", "");
}

/// <summary>
/// «Sin leer» se mide por la hora de la entrada, no por cuántas había: así no le afectan el cambio de
/// día, el recorte del búfer ni reiniciar el gestor (la marca se guarda en disco).
/// </summary>
public static class LogUnread
{
    /// <summary>Cuenta las entradas de nivel advertencia o superior posteriores a <paramref name="seenUntil"/>.</summary>
    public static LogUnreadCounts Count(IEnumerable<LogEntry> entries, DateTimeOffset? seenUntil)
    {
        int errors = 0, warnings = 0;
        foreach (var entry in entries)
        {
            if (entry.IsContinuation || entry.Timestamp is not { } time || (seenUntil is { } seen && time <= seen))
            {
                continue;
            }

            switch (entry.Severity)
            {
                case LogSeverity.Error or LogSeverity.Fatal:
                    errors++;
                    break;
                case LogSeverity.Warning:
                    warnings++;
                    break;
            }
        }

        return new LogUnreadCounts(errors, warnings);
    }

    /// <summary>Hasta dónde se ha visto: la entrada más reciente; sin entradas, <paramref name="now"/>.</summary>
    public static DateTimeOffset SeenUntil(IEnumerable<LogEntry> entries, DateTimeOffset now)
    {
        DateTimeOffset? latest = null;
        foreach (var entry in entries)
        {
            if (entry.Timestamp is { } time && (latest is null || time > latest))
            {
                latest = time;
            }
        }

        return latest ?? now;
    }

    public static LogUnreadBadge Badge(LogUnreadCounts unread)
    {
        if (unread.IsEmpty)
        {
            return LogUnreadBadge.Hidden;
        }

        var toolTip = string.Format(CultureInfo.CurrentCulture, UiStrings.Logs_UnreadToolTip, unread.Errors, unread.Warnings);
        return unread.Errors > 0
            ? new LogUnreadBadge(unread.Errors.ToString("N0", CultureInfo.CurrentCulture), "danger", toolTip)
            : new LogUnreadBadge(unread.Warnings.ToString("N0", CultureInfo.CurrentCulture), "warning", toolTip);
    }
}

/// <summary>Marca «visto hasta» guardada junto a los ajustes del gestor (un instante ISO 8601 por fichero).</summary>
public static class LogSeenStore
{
    public static DateTimeOffset? Load(string path)
    {
        try
        {
            return File.Exists(path) &&
                   DateTimeOffset.TryParse(File.ReadAllText(path).Trim(), CultureInfo.InvariantCulture,
                       DateTimeStyles.RoundtripKind, out var value)
                ? value
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Save(string path, DateTimeOffset value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, value.ToString("O", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sin marca guardada solo se pierde el «leído» entre reinicios del gestor
        }
    }
}

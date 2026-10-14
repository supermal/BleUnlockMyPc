using System.Globalization;

namespace AutoUnlockService.Session;

public enum PromptOutcome { Reject, Snooze, Timeout, Dismissed }

public abstract record HelperCommand
{
    public sealed record Prompt(int Id, double TimeoutSeconds, double SnoozeMinutes) : HelperCommand;
    public sealed record Cancel(int Id) : HelperCommand;
    public sealed record Lock : HelperCommand;
    public sealed record Notify(string Message) : HelperCommand;
}

// Protocolo de texto entre el servicio y el ayudante de sesion.
// Los numeros van siempre en cultura invariante: la maquina puede usar coma decimal
public static class HelperProtocol
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string BuildPrompt(int id, double timeoutSeconds, double snoozeMinutes) =>
        string.Create(Inv, $"PROMPT {id} {timeoutSeconds} {snoozeMinutes}\n");

    public static string BuildCancel(int id) => string.Create(Inv, $"CANCEL {id}\n");

    public static string BuildLock() => "LOCK\n";

    // Un salto de linea dentro del mensaje partiria la orden en dos: se aplana a espacios
    public static string BuildNotify(string message) =>
        $"NOTIFY {message.Replace('\r', ' ').Replace('\n', ' ')}\n";

    public static bool TryParseCommand(string line, out HelperCommand command)
    {
        command = null!;
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return false;

        var parts = trimmed.Split(' ', 2, StringSplitOptions.TrimEntries);
        var verb = parts[0];
        var rest = parts.Length > 1 ? parts[1] : "";

        switch (verb)
        {
            case "LOCK":
                command = new HelperCommand.Lock();
                return true;

            case "NOTIFY":
                if (rest.Length == 0) return false;
                command = new HelperCommand.Notify(rest);
                return true;

            case "CANCEL":
                if (!int.TryParse(rest, NumberStyles.Integer, Inv, out var cancelId)) return false;
                command = new HelperCommand.Cancel(cancelId);
                return true;

            case "PROMPT":
                var args = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (args.Length != 3) return false;
                if (!int.TryParse(args[0], NumberStyles.Integer, Inv, out var id)) return false;
                if (!double.TryParse(args[1], NumberStyles.Float, Inv, out var timeout)) return false;
                if (!double.TryParse(args[2], NumberStyles.Float, Inv, out var snooze)) return false;
                // NaN, infinito o negativo no son plazos validos
                if (!double.IsFinite(timeout) || timeout < 0) return false;
                if (!double.IsFinite(snooze) || snooze < 0) return false;
                command = new HelperCommand.Prompt(id, timeout, snooze);
                return true;

            default:
                return false;
        }
    }

    public static string BuildHello(int sessionId) => string.Create(Inv, $"HELLO {sessionId}\n");

    public static string BuildResponse(int id, PromptOutcome outcome) =>
        string.Create(Inv, $"RESPONSE {id} {outcome.ToString().ToUpperInvariant()}\n");

    public static string BuildLocked() => "LOCKED\n";

    public static bool TryParseResponse(string line, out int id, out PromptOutcome outcome)
    {
        id = 0;
        outcome = PromptOutcome.Timeout;

        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[0] != "RESPONSE") return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, Inv, out id)) return false;

        // Enum.TryParse acepta cifras ("99"): solo se admiten los nombres
        if (!Enum.GetNames<PromptOutcome>().Contains(parts[2], StringComparer.OrdinalIgnoreCase)) return false;
        return Enum.TryParse(parts[2], ignoreCase: true, out outcome);
    }
}

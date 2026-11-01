using System.Globalization;
using BleUnlock.Shared;

namespace BleUnlockService.Session;

public enum PromptOutcome { Reject, Snooze, Timeout, Dismissed }

public abstract record HelperCommand
{
    public sealed record Prompt(int Id, double SnoozeMinutes) : HelperCommand;
    public sealed record Cancel(int Id) : HelperCommand;
    public sealed record Lock : HelperCommand;
    public sealed record Notify(string Message) : HelperCommand;
    public sealed record Language(AppLanguage Value) : HelperCommand;
}

// Protocolo de texto entre el servicio y el ayudante de sesion.
// Los numeros van siempre en cultura invariante: la maquina puede usar coma decimal.
// 判定改版：PROMPT ya no lleva plazo——el aviso queda en pantalla hasta que el usuario
// responda o el servicio lo retire (CANCEL / nuevo Prompt / cierre del canal)
public static class HelperProtocol
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Tope de plazo para el dialogo degradado (WTSSendMessage interpreta 0 como
    // "esperar para siempre": nunca se envia 0)
    public const double MaxTimeoutSeconds = 600;
    public const double MaxSnoozeMinutes = 240;

    public static string BuildPrompt(int id, double snoozeMinutes) =>
        string.Create(Inv, $"PROMPT {id} {snoozeMinutes}\n");

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

            case "LANG":
                if (!AppLanguages.TryParseCode(rest, out var language)) return false;
                command = new HelperCommand.Language(language);
                return true;

            case "CANCEL":
                if (!int.TryParse(rest, NumberStyles.Integer, Inv, out var cancelId)) return false;
                command = new HelperCommand.Cancel(cancelId);
                return true;

            case "PROMPT":
                var args = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (args.Length != 2) return false;
                if (!int.TryParse(args[0], NumberStyles.Integer, Inv, out var id)) return false;
                if (!double.TryParse(args[1], NumberStyles.Float, Inv, out var snooze)) return false;
                // NaN, infinito, negativo o por encima del maximo no son plazos validos
                if (!double.IsFinite(snooze) || snooze < 0 || snooze > MaxSnoozeMinutes) return false;
                command = new HelperCommand.Prompt(id, snooze);
                return true;

            default:
                return false;
        }
    }

    // Idioma de interfaz que debe usar el ayudante (el servicio decide; el ayudante no ve la configuracion)
    public static string BuildLanguage(AppLanguage language) => $"LANG {AppLanguages.ToCode(language)}\n";

    // HELLO lleva la cultura de interfaz del usuario: el servicio corre como SYSTEM y no la conoce.
    // Sin cultura (ayudante antiguo) sigue siendo valido
    public static string BuildHello(int sessionId, string? uiCulture = null) =>
        string.IsNullOrWhiteSpace(uiCulture)
            ? string.Create(Inv, $"HELLO {sessionId}\n")
            : string.Create(Inv, $"HELLO {sessionId} {uiCulture.Trim()}\n");

    public static bool TryParseHello(string line, out int sessionId, out string? uiCulture)
    {
        sessionId = 0;
        uiCulture = null;

        var parts = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] != "HELLO") return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, Inv, out sessionId)) return false;

        uiCulture = parts.Length == 3 ? parts[2] : null;
        return true;
    }

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

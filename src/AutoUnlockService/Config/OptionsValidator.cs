using System.Globalization;

namespace AutoUnlockService.Config;

public sealed record ValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static ValidationResult Ok() => new(true, []);
    public static ValidationResult Invalid(IReadOnlyList<string> errors) => new(false, errors);
}

public static class OptionsValidator
{
    // Quita espacios, dos puntos y el prefijo 0x; exige exactamente 32 caracteres hexadecimales
    public static string? NormalizeIrk(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var cleaned = raw.Trim().Replace(":", "").Replace(" ", "").Replace("-", "");
        if (cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[2..];

        if (cleaned.Length != 32) return null;
        foreach (var c in cleaned)
            if (!Uri.IsHexDigit(c)) return null;

        return cleaned.ToLowerInvariant();
    }

    // Igual que el IRK pero de 12 caracteres; null o vacio significa "no configurado"
    public static ulong? NormalizeAddress(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var cleaned = raw.Trim().Replace(":", "").Replace("-", "").Replace(" ", "");
        if (cleaned.Length != 12) return null;
        return ulong.TryParse(cleaned, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
            ? v : null;
    }

    public static ValidationResult Validate(AutoUnlockOptions o)
    {
        var errors = new List<string>();

        if (NormalizeIrk(o.Device.Irk) is null)
            errors.Add("Device.Irk 必须是 32 个十六进制字符");

        if (!string.IsNullOrWhiteSpace(o.Device.IdentityAddress) &&
            NormalizeAddress(o.Device.IdentityAddress) is null)
            errors.Add("Device.IdentityAddress 必须是合法的 6 字节 MAC 地址");

        if (string.IsNullOrWhiteSpace(o.Credentials.UserName))
            errors.Add("Credentials.UserName 不能为空");

        // Un caracter de control (salto de linea, retorno de carro, tabulador...) rompe el encuadre
        // por lineas del pipe hacia el CP.
        // Los mensajes nombran el campo pero nunca incluyen su valor
        if ((o.Credentials.UserName ?? "").Any(char.IsControl))
            errors.Add("Credentials.UserName 不能包含控制字符（换行、回车、制表符等）");
        if ((o.Credentials.Password ?? "").Any(char.IsControl))
            errors.Add("Credentials.Password 不能包含控制字符（换行、回车、制表符等）");

        var p = o.Proximity;
        if (p.UnlockThresholdDbm <= p.LockThresholdDbm)
            errors.Add("Proximity.UnlockThresholdDbm 必须大于 LockThresholdDbm，否则死区为负");

        if (p.LockWindowSeconds <= 0)   errors.Add("Proximity.LockWindowSeconds 必须为正数");
        if (p.UnlockWindowSeconds <= 0) errors.Add("Proximity.UnlockWindowSeconds 必须为正数");
        if (p.StartupGraceSeconds < 0)  errors.Add("Proximity.StartupGraceSeconds 不能为负数");
        if (p.UnlockPendingTimeoutSeconds <= 0)
            errors.Add("Proximity.UnlockPendingTimeoutSeconds 必须为正数");
        if (p.MinSamples < 1)           errors.Add("Proximity.MinSamples 必须大于等于 1");

        var n = o.Notification;
        if (n.PromptTimeoutSeconds <= 0) errors.Add("Notification.PromptTimeoutSeconds 必须为正数");
        if (n.RejectSnoozeSeconds <= 0)  errors.Add("Notification.RejectSnoozeSeconds 必须为正数");
        if (n.SnoozeMinutes <= 0)        errors.Add("Notification.SnoozeMinutes 必须为正数");

        return errors.Count == 0 ? ValidationResult.Ok() : ValidationResult.Invalid(errors);
    }
}

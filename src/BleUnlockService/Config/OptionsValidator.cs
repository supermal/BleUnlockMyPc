using System.Globalization;
using BleUnlockService.Session;
using BleUnlockService.Resources;

namespace BleUnlockService.Config;

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

    public static ValidationResult Validate(BleUnlockOptions o)
    {
        var errors = new List<string>();

        // Vacío = aún sin dispositivo (equipo recién instalado): no es un error, el servicio arranca
        // sin vigilar a nadie (ConfigCompleteness). Solo un valor escrito y mal formado es un error
        if (!string.IsNullOrWhiteSpace(o.Device.Irk) && NormalizeIrk(o.Device.Irk) is null)
            errors.Add(ServiceStrings.Validation_IrkFormat);

        if (!string.IsNullOrWhiteSpace(o.Device.IdentityAddress) &&
            NormalizeAddress(o.Device.IdentityAddress) is null)
            errors.Add(ServiceStrings.Validation_AddressFormat);

        // Usuario o clave vacíos tampoco son un error: sin credenciales el servicio solo bloquea

        // Un caracter de control (salto de linea, retorno de carro, tabulador...) rompe el encuadre
        // por lineas del pipe hacia el CP.
        // Los mensajes nombran el campo pero nunca incluyen su valor
        if ((o.Credentials.UserName ?? "").Any(char.IsControl))
            errors.Add(ServiceStrings.Validation_UserNameControlChars);
        if ((o.Credentials.Password ?? "").Any(char.IsControl))
            errors.Add(ServiceStrings.Validation_PasswordControlChars);

        var p = o.Proximity;
        if (p.UnlockThresholdDbm <= p.LockThresholdDbm)
            errors.Add(ServiceStrings.Validation_ThresholdOrder);

        // 批次个数（触发读数不计入，全数达标判定）：下限 1，上限防止收集阶段拖得过长
        if (p.SampleIntervalMs is < 0 or > 2000)
            errors.Add(ServiceStrings.Validation_SampleIntervalRange);

        if (p.UnlockSampleCount is < 1 or > 20)
            errors.Add(ServiceStrings.Validation_UnlockCountRange);
        if (p.LockPromptSampleCount is < 1 or > 50)
            errors.Add(ServiceStrings.Validation_LockPromptCountRange);
        if (p.LockConfirmSampleCount is < 1 or > 50)
            errors.Add(ServiceStrings.Validation_LockConfirmCountRange);

        if (p.StartupGraceSeconds < 0)  errors.Add(ServiceStrings.Validation_GraceNegative);
        if (p.UnlockPendingTimeoutSeconds <= 0)
            errors.Add(ServiceStrings.Validation_PendingTimeoutPositive);

        var n = o.Notification;
        // El maximo vive en HelperProtocol: el ayudante rechaza lo que lo supere,
        // y un aviso rechazado en silencio acabaria en bloqueo sin que el usuario lo viera
        if (n.RejectSnoozeSeconds <= 0)  errors.Add(ServiceStrings.Validation_RejectSnoozePositive);
        if (!(n.SnoozeMinutes > 0))
            errors.Add(ServiceStrings.Validation_SnoozePositive);
        else if (n.SnoozeMinutes > HelperProtocol.MaxSnoozeMinutes)
            errors.Add(string.Format(CultureInfo.InvariantCulture,
                ServiceStrings.Validation_SnoozeMax, HelperProtocol.MaxSnoozeMinutes));

        return errors.Count == 0 ? ValidationResult.Ok() : ValidationResult.Invalid(errors);
    }
}

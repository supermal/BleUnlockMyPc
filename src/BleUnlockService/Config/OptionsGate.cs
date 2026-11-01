using BleUnlock.Shared;
using BleUnlockService.Resources;

namespace BleUnlockService.Config;

public sealed record OptionsChange(
    BleUnlockOptions Options, bool DeviceChanged, bool CredentialsChanged);

// Filtra las recargas en caliente: antirrebote, resolucion de secretos (dpapi), validacion y, si no
// pasa, conservar la configuracion que ya estaba funcionando
public sealed class OptionsGate : IDisposable
{
    private readonly ILogger<OptionsGate> _log;
    private readonly TimeProvider _time;
    private readonly TimeSpan _debounce;
    private readonly string? _statusPath;
    private readonly object _lock = new();

    private ITimer? _timer;
    private BleUnlockOptions? _pending;
    private bool _disposed;

    // statusPath: dónde dejar el resultado de cada recarga para el gestor; null = no se escribe
    public OptionsGate(
        BleUnlockOptions initial, ILogger<OptionsGate> log, TimeProvider time, TimeSpan debounce,
        string? statusPath = null)
    {
        Current = initial;
        _log = log;
        _time = time;
        _debounce = debounce;
        _statusPath = statusPath;
    }

    public BleUnlockOptions Current { get; private set; }

    public event Action<OptionsChange>? Changed;

    // IOptionsMonitor puede llamar varias veces por un solo guardado, y a veces con el
    // fichero a medio escribir: por eso no se aplica nada hasta que el antirrebote vence
    public void Submit(BleUnlockOptions candidate)
    {
        lock (_lock)
        {
            // Tras Dispose no se crean temporizadores nuevos que nadie limpiaria
            if (_disposed) return;

            _pending = candidate;
            _timer?.Dispose();
            _timer = _time.CreateTimer(_ => Apply(), null, _debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Apply()
    {
        OptionsChange? change = null;

        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;

            var candidate = _pending;
            _pending = null;
            if (candidate is null) return;

            // 敏感值解析（dpapi 密文 → 明文）：密文解不开等同校验失败，走同一「保留原配置」路径；
            // 明文存储仅记警告（管理端保存时自动加密）
            OptionsSecrets.TryResolve(candidate, out var secretWarnings, out var secretErrors);
            foreach (var warning in secretWarnings)
                _log.LogWarning("{Warning}", warning);
            if (secretErrors.Count > 0)
            {
                foreach (var error in secretErrors)
                    _log.LogError(ServiceStrings.Log_ConfigRejected, error);
                WriteStatus(ReloadStatus.Create(ReloadStatusFile.Rejected, message: string.Join("\n", secretErrors)));
                return;
            }

            var result = OptionsValidator.Validate(candidate);
            if (!result.IsValid)
            {
                // Nunca se degrada a la configuracion nueva: seguir con la que funcionaba
                foreach (var error in result.Errors)
                    _log.LogError(ServiceStrings.Log_ConfigRejected, error);
                WriteStatus(ReloadStatus.Create(ReloadStatusFile.Rejected, message: string.Join("\n", result.Errors)));
                return;
            }

            bool deviceChanged = DeviceDiffers(Current, candidate);
            bool credentialsChanged = CredentialsDiffer(Current, candidate);
            if (!deviceChanged && !credentialsChanged && !OtherDiffers(Current, candidate))
            {
                WriteStatus(ReloadStatus.Create(ReloadStatusFile.Unchanged));
                return;
            }

            Current = candidate;
            change = new OptionsChange(candidate, deviceChanged, credentialsChanged);

            // Nunca se registran valores de credenciales, solo que cambiaron
            _log.LogInformation(ServiceStrings.Log_ConfigReloaded,
                deviceChanged, credentialsChanged);
            WriteStatus(ReloadStatus.Create(ReloadStatusFile.Applied, deviceChanged, credentialsChanged));
        }

        if (change is not null) Changed?.Invoke(change);
    }

    // Un fallo al escribir el estado no debe afectar a la recarga: el gestor acabará por agotar su espera
    private void WriteStatus(ReloadStatus status)
    {
        if (_statusPath is null) return;
        try
        {
            ReloadStatusFile.Write(_statusPath, status);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ServiceStrings.Log_ReloadStatusWriteFailed, ex.Message);
        }
    }

    private static bool DeviceDiffers(BleUnlockOptions a, BleUnlockOptions b) =>
        !string.Equals(OptionsValidator.NormalizeIrk(a.Device.Irk),
                       OptionsValidator.NormalizeIrk(b.Device.Irk), StringComparison.Ordinal) ||
        a.Device.IrkByteOrder != b.Device.IrkByteOrder ||
        OptionsValidator.NormalizeAddress(a.Device.IdentityAddress) !=
        OptionsValidator.NormalizeAddress(b.Device.IdentityAddress);

    private static bool CredentialsDiffer(BleUnlockOptions a, BleUnlockOptions b) =>
        !string.Equals(a.Credentials.UserName, b.Credentials.UserName, StringComparison.Ordinal) ||
        !string.Equals(a.Credentials.Password, b.Credentials.Password, StringComparison.Ordinal);

    private static bool OtherDiffers(BleUnlockOptions a, BleUnlockOptions b) =>
        a.Proximity.LockThresholdDbm != b.Proximity.LockThresholdDbm ||
        a.Proximity.UnlockThresholdDbm != b.Proximity.UnlockThresholdDbm ||
        a.Proximity.SampleIntervalMs != b.Proximity.SampleIntervalMs ||
        a.Proximity.UnlockSampleCount != b.Proximity.UnlockSampleCount ||
        a.Proximity.LockPromptSampleCount != b.Proximity.LockPromptSampleCount ||
        a.Proximity.LockConfirmSampleCount != b.Proximity.LockConfirmSampleCount ||
        a.Proximity.StartupGraceSeconds != b.Proximity.StartupGraceSeconds ||
        a.Proximity.UnlockPendingTimeoutSeconds != b.Proximity.UnlockPendingTimeoutSeconds ||
        a.Notification.Mode != b.Notification.Mode ||
        a.Notification.RejectSnoozeSeconds != b.Notification.RejectSnoozeSeconds ||
        a.Notification.SnoozeMinutes != b.Notification.SnoozeMinutes ||
        LanguageKey(a) != LanguageKey(b);

    // Se compara el idioma normalizado: " EN " y "en" son el mismo ajuste
    private static string LanguageKey(BleUnlockOptions o) =>
        AppLanguages.TryParseCode(o.Ui?.Language, out var language)
            ? AppLanguages.ToCode(language)
            : AppLanguages.SystemSetting;

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}

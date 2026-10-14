namespace AutoUnlockService.Config;

public sealed record OptionsChange(
    AutoUnlockOptions Options, bool DeviceChanged, bool CredentialsChanged);

// Filtra las recargas en caliente: antirrebote, validacion y, si no pasa,
// conservar la configuracion que ya estaba funcionando
public sealed class OptionsGate : IDisposable
{
    private readonly ILogger<OptionsGate> _log;
    private readonly TimeProvider _time;
    private readonly TimeSpan _debounce;
    private readonly object _lock = new();

    private ITimer? _timer;
    private AutoUnlockOptions? _pending;

    public OptionsGate(
        AutoUnlockOptions initial, ILogger<OptionsGate> log, TimeProvider time, TimeSpan debounce)
    {
        Current = initial;
        _log = log;
        _time = time;
        _debounce = debounce;
    }

    public AutoUnlockOptions Current { get; private set; }

    public event Action<OptionsChange>? Changed;

    // IOptionsMonitor puede llamar varias veces por un solo guardado, y a veces con el
    // fichero a medio escribir: por eso no se aplica nada hasta que el antirrebote vence
    public void Submit(AutoUnlockOptions candidate)
    {
        lock (_lock)
        {
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

            var result = OptionsValidator.Validate(candidate);
            if (!result.IsValid)
            {
                // Nunca se degrada a la configuracion nueva: seguir con la que funcionaba
                foreach (var error in result.Errors)
                    _log.LogError("配置校验失败，已保留原配置：{Error}", error);
                return;
            }

            bool deviceChanged = DeviceDiffers(Current, candidate);
            bool credentialsChanged = CredentialsDiffer(Current, candidate);
            if (!deviceChanged && !credentialsChanged && !OtherDiffers(Current, candidate)) return;

            Current = candidate;
            change = new OptionsChange(candidate, deviceChanged, credentialsChanged);

            // Nunca se registran valores de credenciales, solo que cambiaron
            _log.LogInformation("配置已重载（设备变更={Device}，凭据变更={Credentials}）",
                deviceChanged, credentialsChanged);
        }

        if (change is not null) Changed?.Invoke(change);
    }

    private static bool DeviceDiffers(AutoUnlockOptions a, AutoUnlockOptions b) =>
        !string.Equals(OptionsValidator.NormalizeIrk(a.Device.Irk),
                       OptionsValidator.NormalizeIrk(b.Device.Irk), StringComparison.Ordinal) ||
        a.Device.IrkByteOrder != b.Device.IrkByteOrder ||
        OptionsValidator.NormalizeAddress(a.Device.IdentityAddress) !=
        OptionsValidator.NormalizeAddress(b.Device.IdentityAddress);

    private static bool CredentialsDiffer(AutoUnlockOptions a, AutoUnlockOptions b) =>
        !string.Equals(a.Credentials.UserName, b.Credentials.UserName, StringComparison.Ordinal) ||
        !string.Equals(a.Credentials.Password, b.Credentials.Password, StringComparison.Ordinal);

    private static bool OtherDiffers(AutoUnlockOptions a, AutoUnlockOptions b) =>
        a.Proximity.LockThresholdDbm != b.Proximity.LockThresholdDbm ||
        a.Proximity.LockWindowSeconds != b.Proximity.LockWindowSeconds ||
        a.Proximity.UnlockThresholdDbm != b.Proximity.UnlockThresholdDbm ||
        a.Proximity.UnlockWindowSeconds != b.Proximity.UnlockWindowSeconds ||
        a.Proximity.MinSamples != b.Proximity.MinSamples ||
        a.Proximity.StartupGraceSeconds != b.Proximity.StartupGraceSeconds ||
        a.Proximity.UnlockPendingTimeoutSeconds != b.Proximity.UnlockPendingTimeoutSeconds ||
        a.Notification.Mode != b.Notification.Mode ||
        a.Notification.PromptTimeoutSeconds != b.Notification.PromptTimeoutSeconds ||
        a.Notification.RejectSnoozeSeconds != b.Notification.RejectSnoozeSeconds ||
        a.Notification.SnoozeMinutes != b.Notification.SnoozeMinutes;

    public void Dispose()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}

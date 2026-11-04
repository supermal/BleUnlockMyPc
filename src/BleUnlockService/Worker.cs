using BleUnlockService.Ble;
using BleUnlockService.Config;
using BleUnlockService.Core;
using BleUnlockService.Credentials;
using BleUnlockService.Management;
using BleUnlockService.Notifications;
using BleUnlockService.Session;
using BleUnlockService.Resources;

namespace BleUnlockService;

public sealed class Worker(
    ILogger<Worker> log,
    TimeProvider time,
    OptionsGate options,
    IRssiSource source,
    DeviceMatcher matcher,
    CredentialValidator validator,
    CredentialPipeServer credentialPipe,
    HelperPipeServer helperPipe,
    HelperLauncher launcher,
    ResilientNotifier notifier,
    IScreenLocker locker,
    SessionMonitor sessions,
    TelemetryHub telemetry) : BackgroundService
{
    private static readonly TimeSpan EvaluateInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HistoryRetention = TimeSpan.FromSeconds(120);

    // Tope de envios seguidos sin resultado: protege de la via lenta al bloqueo de cuenta
    internal const int MaxCredentialAttempts = 3;

    private readonly RssiHistory _history = new(HistoryRetention, time);
    private readonly CredentialAttemptBudget _credentialBudget = new(MaxCredentialAttempts);
    private readonly SampleResampler _resampler = new(time);
    private readonly object _gate = new();
    private ProximityStateMachine _machine = null!;
    private DateTimeOffset _lastSummary;
    private long _lastMatchTicks;
    private bool _scanStalled;

    // Nombre canonico («DOMINIO\usuario») del usuario configurado, con el texto del que salio:
    // solo vale mientras el fichero siga diciendo lo mismo
    private volatile ResolvedLogonName? _logonName;

    private sealed record ResolvedLogonName(string Configured, string Resolved);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var current = options.Current;
        matcher.Configure(current.Device);

        // El estado inicial importa: si la pantalla ya estaba bloqueada al arrancar,
        // la maquina debe exigir "salir y volver" en vez de creer que esta desbloqueada
        bool lockedAtStart = false;
        if (WtsNative.TryIsActiveConsoleSessionLocked(out var sessionLocked))
        {
            lockedAtStart = sessionLocked;
            log.LogInformation(ServiceStrings.Log_ScreenAtStartup, sessionLocked ? ServiceStrings.State_Locked : ServiceStrings.State_Unlocked);
        }
        else
        {
            log.LogWarning(ServiceStrings.Log_ScreenStateUnknownAtStartup);
        }

        _machine = new ProximityStateMachine(time, current.Proximity, current.Notification, lockedAtStart);
        telemetry.OnStateChange(_machine.State);   // 遥测：初始状态没有转换事件，先行播种
        _lastSummary = time.GetUtcNow();
        Volatile.Write(ref _lastMatchTicks, time.GetUtcNow().UtcTicks);

        if (current.Notification.Mode == NotifierMode.Dialog)
            notifier.Degrade(ServiceStrings.Reason_DialogModeConfigured);

        helperPipe.Start(ct);
        launcher.Start(ct);
        credentialPipe.Start(ct);

        source.SampleReceived += OnSample;
        source.StatusChanged += OnStatus;
        credentialPipe.AuthResultReceived += OnAuthResult;
        credentialPipe.Connected += OnCredConnected;
        credentialPipe.Disconnected += OnCredDisconnected;
        helperPipe.Connected += OnHelperConnected;
        helperPipe.Disconnected += OnHelperDisconnected;
        sessions.SessionLocked += OnSessionLocked;
        sessions.SessionUnlocked += OnSessionUnlocked;
        sessions.Resumed += OnResumed;
        options.Changed += OnOptionsChanged;
        Action<string> degradeHandler = notifier.Degrade;
        launcher.DegradeRequested += degradeHandler;

        try
        {
            // Se escanea aunque aún no haya dispositivo: sin IRK nada coincide y la máquina no hace nada,
            // y así elegirlo en el gestor surte efecto en caliente, sin reiniciar el servicio
            await source.StartAsync(ct);

            LogSetupState(current);
            ApplyCredentials(current.Credentials);
            var resampling = RunResamplerAsync(ct);

            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(EvaluateInterval, time, ct); }
                catch (OperationCanceledException) { break; }

                Transition(() => _machine.Evaluate(_history));
                PublishBatchProgress();
                WriteSummaryIfDue();
            }

            await resampling;
        }
        finally
        {
            // Todas las suscripciones son a singletons que sobreviven al Worker: no se deja ninguna colgando
            source.SampleReceived -= OnSample;
            source.StatusChanged -= OnStatus;
            credentialPipe.AuthResultReceived -= OnAuthResult;
            credentialPipe.Connected -= OnCredConnected;
            credentialPipe.Disconnected -= OnCredDisconnected;
            helperPipe.Connected -= OnHelperConnected;
            helperPipe.Disconnected -= OnHelperDisconnected;
            sessions.SessionLocked -= OnSessionLocked;
            sessions.SessionUnlocked -= OnSessionUnlocked;
            sessions.Resumed -= OnResumed;
            options.Changed -= OnOptionsChanged;
            launcher.DegradeRequested -= degradeHandler;
            await source.StopAsync();
        }
    }

    private void OnSample(AdvertisementSample sample)
    {
        if (!matcher.IsTarget(sample)) return;
        // 哨兵值不是测量结果：放进来会凭空凑出「信号变弱」（实测曾疑似造成误弹窗），
        // 也不该出现在遥测曲线里
        if (!HasUsableRssi(sample.Rssi)) return;
        Volatile.Write(ref _lastMatchTicks, time.GetUtcNow().UtcTicks);

        // 定时取样时读数先交给取样器，由 RunResamplerAsync 按间隔计入；0 = 每条广播直接计入
        if (options.Current.Proximity.SampleIntervalMs > 0)
        {
            _resampler.Offer(sample.Rssi);
            return;
        }

        _history.Add(sample.Rssi, sample.Timestamp);
        telemetry.OnSample(sample.Timestamp.ToUnixTimeMilliseconds(), sample.Rssi);
    }

    // Cada SampleIntervalMs registra una lectura: la más reciente recibida o, si no llegó ninguna, la anterior
    // repetida (ver SampleResampler). El intervalo se relee en cada vuelta: la recarga en caliente surte efecto
    private async Task RunResamplerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var proximity = options.Current.Proximity;
            try
            {
                if (proximity.SampleIntervalMs <= 0)
                {
                    _resampler.Reset();
                    await Task.Delay(TimeSpan.FromMilliseconds(500), time, ct);
                    continue;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(proximity.SampleIntervalMs), time, ct);
            }
            catch (OperationCanceledException) { break; }

            var noSignal = options.Current.Proximity.NoSignalDepartureSeconds;
            var value = _resampler.Take(
                holdAllowed: !Volatile.Read(ref _scanStalled),
                holdLimit: noSignal > 0 ? TimeSpan.FromSeconds(noSignal) : TimeSpan.Zero);
            if (value is not { } rssi) continue;

            var now = time.GetUtcNow();
            _history.Add(rssi, now);
            telemetry.OnSample(now.ToUnixTimeMilliseconds(), rssi);
        }
    }

    // BLE 规范把 -127/+127 一带预留为「RSSI 不可用」：不能当作读数
    internal static bool HasUsableRssi(short rssi) => rssi is > -127 and < 127;

    private void OnStatus(ScanStatus status)
    {
        log.LogInformation(ServiceStrings.Log_BleStatus, status.Text);
        telemetry.OnScanStatus(status);

        // Solo stalled/stalledFailed y running cambian el estado del receptor; noAdapter o radioOff
        // durante un reinicio del adaptador forman parte de la recuperación y no la terminan
        switch (status.Code)
        {
            case ScanStatus.Stalled:
                Volatile.Write(ref _scanStalled, true);
                Transition(() => _machine.OnScanHealthChanged(stalled: true));
                _ = NotifyScanAsync(ServiceStrings.Notify_ScanStalled);
                break;

            case ScanStatus.StalledFailed:
                Volatile.Write(ref _scanStalled, true);
                Transition(() => _machine.OnScanHealthChanged(stalled: true));
                _ = NotifyScanAsync(ServiceStrings.Notify_ScanStalledFailed);
                break;

            case ScanStatus.Running when Volatile.Read(ref _scanStalled):
                Volatile.Write(ref _scanStalled, false);
                Transition(() => _machine.OnScanHealthChanged(stalled: false));
                break;
        }
    }

    private async Task NotifyScanAsync(string message)
    {
        // Sin dispositivo elegido el receptor no vigila a nadie: el aviso solo confundiría (queda en el registro)
        if (!ConfigCompleteness.HasDevice(options.Current.Device)) return;

        try { await notifier.NotifyAsync(message, CancellationToken.None); }
        catch (Exception ex) { log.LogError(ex, ServiceStrings.Log_ActionFailed, "NotifyScan"); }
    }

    // Telemetría: el progreso del lote se lee bajo el mismo candado que las transiciones
    private void PublishBatchProgress()
    {
        BatchProgress? progress;
        lock (_gate) progress = _machine.Progress;
        telemetry.OnBatchProgress(progress);
    }

    private void OnHelperConnected(int sessionId)
    {
        telemetry.OnHelperConnection(true);
        _ = RemindSetupAsync();
    }

    private void OnHelperDisconnected() => telemetry.OnHelperConnection(false);

    private void OnCredConnected() => telemetry.OnCredConnection(true);

    private void OnCredDisconnected() => telemetry.OnCredConnection(false);

    private void OnResumed()
    {
        // El reloj puede haber saltado horas: el historico ya no representa nada
        log.LogInformation(ServiceStrings.Log_ResumedFromSleep);
        _resampler.Reset();
        _history.Clear();
        telemetry.OnHistoryReset();   // 遥测：历史作废，管理端据 gen 变化清空曲线
        Transition(() => _machine.OnResume());
    }

    private void OnSessionLocked() => Transition(() => _machine.OnSessionEvent(SessionEvent.Lock));

    private void OnSessionUnlocked()
    {
        // Un inicio de sesion interactivo correcto reinicia el contador de intentos
        // fallidos, igual que hace Windows: es justo devolver el presupuesto
        _credentialBudget.Reset();
        Transition(() => _machine.OnSessionEvent(SessionEvent.Unlock));
    }

    private void OnAuthResult(AuthResult result)
    {
        // Llego respuesta del CP: la via lenta al bloqueo (RESULT que nunca llega) no aplica
        _credentialBudget.Reset();
        Transition(() => _machine.OnAuthResult(result));
    }

    private void OnOptionsChanged(OptionsChange change)
    {
        try
        {
            lock (_gate)
            {
                _machine.Proximity = change.Options.Proximity;
                _machine.Notification = change.Options.Notification;
            }

            if (change.DeviceChanged)
            {
                matcher.Configure(change.Options.Device);
                _resampler.Reset();
                _history.Reset();
                telemetry.OnHistoryReset();   // 遥测：设备变更，历史作废，管理端据 gen 变化清空曲线
                _credentialBudget.Reset();
                log.LogInformation(ServiceStrings.Log_TargetDeviceChanged);
            }

            if (change.CredentialsChanged)
                ApplyCredentials(change.Options.Credentials);

            if (change.DeviceChanged || change.CredentialsChanged)
                LogSetupState(change.Options);
        }
        catch (Exception ex)
        {
            // Corre en el hilo del temporizador del OptionsGate: una excepcion sin
            // capturar aqui mataria el proceso
            log.LogError(ex, ServiceStrings.Log_ApplyConfigChangeFailed);
        }
    }

    // La prevalidacion es la primera mitad del doble seguro. Sin usuario o clave no hay nada que
    // prevalidar (LogonUser fallaría y suspendería todo): se pasa a «solo bloquear»
    private void ApplyCredentials(CredentialOptions credentials)
    {
        if (!ConfigCompleteness.HasCredentials(credentials))
        {
            Transition(() => _machine.OnCredentialsMissing());
            return;
        }

        var logonName = ResolveLogonName(credentials.UserName, out var accountNotFound);

        // Solo una negativa explicita de Windows suspende; «no se pudo comprobar» (sin controlador
        // de dominio al arrancar, directivas que niegan el inicio por red) deja decidir al desbloqueo real
        bool ok = validator.Validate(logonName, credentials.Password) != CredentialCheck.Invalid;
        if (ok) _credentialBudget.Reset();

        // Rechazada y desconocida para Windows: no es la clave, es que la cuenta no existe en este equipo
        // (p. ej. un correo de Outlook con el que nunca se ha iniciado sesión aquí)
        string? reason = null;
        if (!ok && accountNotFound)
        {
            reason = string.Format(ServiceStrings.Reason_AccountNotFound, credentials.UserName.Trim());
            log.LogError(ServiceStrings.Log_AccountNotFound, credentials.UserName.Trim());
        }

        Transition(() => _machine.OnCredentialsChanged(ok, reason));
    }

    private string ResolveLogonName(string configured, out bool notFound)
    {
        var resolved = AccountNameResolver.TryResolve(configured, out var error, out notFound);
        if (resolved is null)
        {
            _logonName = null;
            log.LogWarning(ServiceStrings.Log_AccountResolveFailed, configured, error ?? "");
            return configured.Trim();
        }

        _logonName = new ResolvedLogonName(configured, resolved);
        if (!string.Equals(configured.Trim(), resolved, StringComparison.OrdinalIgnoreCase))
            log.LogInformation(ServiceStrings.Log_AccountResolved, configured, resolved);
        return resolved;
    }

    // El nombre que se manda al CP: el canonico si corresponde al usuario actual del fichero
    private string LogonNameFor(string configured) =>
        _logonName is { } name && name.Configured == configured ? name.Resolved : configured.Trim();

    private void LogSetupState(BleUnlockOptions current)
    {
        switch (ConfigCompleteness.Evaluate(current))
        {
            case SetupState.NoDevice:
                log.LogWarning(ServiceStrings.Log_SetupNoDevice);
                break;
            case SetupState.NoCredentials:
                log.LogWarning(ServiceStrings.Log_SetupNoCredentials);
                break;
        }
    }

    // Cada vez que el asistente se conecta (en la práctica, en cada inicio de sesión) se recuerda lo
    // que falta por configurar. Así el aviso llega aunque el servicio arrancase antes que la sesión
    private async Task RemindSetupAsync()
    {
        var message = ConfigCompleteness.Evaluate(options.Current) switch
        {
            SetupState.NoDevice => ServiceStrings.Notify_SetupNoDevice,
            SetupState.NoCredentials => ServiceStrings.Notify_SetupNoCredentials,
            _ => null
        };
        if (message is null) return;

        try
        {
            // El asistente acaba de conectarse: se le deja un momento para que esté listo para mostrar avisos
            await Task.Delay(TimeSpan.FromSeconds(3), time);
            await notifier.NotifyAsync(message, CancellationToken.None);
        }
        catch (Exception ex) { log.LogError(ex, ServiceStrings.Log_ActionFailed, "RemindSetup"); }
    }

    // La maquina de estados no es thread-safe y aqui la tocan tres hilos distintos:
    // el temporizador de evaluacion, los callbacks de los dos canales y los eventos
    // de sesion. El candado cubre solo el calculo de la transicion; la accion
    // resultante se despacha fuera para no bloquear a los demas
    private void Transition(Func<StateChange?> step)
    {
        StateChange? change;
        lock (_gate) { change = step(); }

        if (change is null) return;

        log.LogInformation(ServiceStrings.Log_StateTransition, change.From, change.To, change.Reason);
        telemetry.OnStateChange(change.To);   // 遥测：状态变更（旁路，不影响控制流）
        _ = DispatchAsync(change);
    }

    private async Task DispatchAsync(StateChange change)
    {
        try
        {
            switch (change.Action)
            {
                case ProximityAction.ShowLockPrompt:
                    var n = options.Current.Notification;
                    var outcome = await notifier.PromptAsync(n.SnoozeMinutes, CancellationToken.None);
                    Transition(() => _machine.OnPromptResponse(ToPromptResponse(outcome)));
                    break;

                case ProximityAction.CancelLockPrompt:
                    await notifier.CancelAsync();
                    break;

                case ProximityAction.LockScreen:
                    await notifier.CancelAsync();
                    await locker.LockAsync(CancellationToken.None);
                    break;

                case ProximityAction.SendCredentials:
                    await TrySendCredentialsAsync();
                    break;

                case ProximityAction.CancelLockPromptAndNotifySuspended:
                    // Salir de PendingLock hacia Suspended: retirar el aviso Y notificar
                    await notifier.CancelAsync();
                    await notifier.NotifyAsync(change.Reason + ServiceStrings.Notify_FixConfigSuffix,
                        CancellationToken.None);
                    break;

                case ProximityAction.NotifySuspended:
                    await notifier.NotifyAsync(change.Reason + ServiceStrings.Notify_FixConfigSuffix,
                        CancellationToken.None);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // CancelAsync retira el aviso pendiente y su PromptAsync termina asi:
            // es un desenlace normal ("la senal volvio"), no una averia.
            // No hay respuesta de usuario que entregar a la maquina de estados
            log.LogDebug(ServiceStrings.Log_PromptWithdrawn, change.Action);
        }
        catch (Exception ex)
        {
            log.LogError(ex, ServiceStrings.Log_ActionFailed, change.Action);
        }
    }

    private async Task TrySendCredentialsAsync()
    {
        if (_credentialBudget.IsExhausted)
        {
            // El estado LockedArmed seguira pidiendo el envio cada segundo: solo interesa
            // en Debug; el aviso visible lo da el resumen periodico
            log.LogDebug(ServiceStrings.Log_CredentialBudgetExhausted);
            return;
        }

        var c = options.Current.Credentials;
        if (await credentialPipe.SendCredentialsAsync(LogonNameFor(c.UserName), c.Password, CancellationToken.None)
            && _credentialBudget.NoteAttempt())
        {
            log.LogWarning(
                ServiceStrings.Log_CredentialRetriesPaused,
                MaxCredentialAttempts);
        }
    }

    private static PromptResponse ToPromptResponse(PromptOutcome outcome) => outcome switch
    {
        PromptOutcome.Reject => PromptResponse.Reject,
        PromptOutcome.Snooze => PromptResponse.Snooze,
        PromptOutcome.Dismissed => PromptResponse.Dismissed,
        _ => PromptResponse.Timeout
    };

    // Sin este resumen habria que activar el nivel Debug para ajustar los umbrales,
    // y eso llena el registro con una linea por emision recibida
    private void WriteSummaryIfDue()
    {
        var now = time.GetUtcNow();
        if (now - _lastSummary < SummaryInterval) return;
        _lastSummary = now;

        // 固定 30 秒窗口仅用于观察：判定本身不再依赖时间窗
        var stats = _history.InWindow(now, TimeSpan.FromSeconds(30));
        var sinceMatch = TimeSpan.FromTicks(now.UtcTicks - Volatile.Read(ref _lastMatchTicks)).TotalSeconds;
        var batch = _machine.BatchProgressText;

        log.LogInformation(
            ServiceStrings.Log_Summary,
            _machine.State, batch.Length == 0 ? "" : string.Format(ServiceStrings.Log_SummaryBatch, batch), stats.Count,
            stats.Count == 0 ? double.NaN : stats.Average, sinceMatch);

        // Con el receptor colgado no llega nada de nadie: la causa ya está en el registro y no es el IRK
        // Sin dispositivo elegido no hay nada que encontrar: la advertencia de configuración ya se dio
        if (sinceMatch > TimeSpan.FromMinutes(5).TotalSeconds && !Volatile.Read(ref _scanStalled)
            && ConfigCompleteness.HasDevice(options.Current.Device))
            log.LogWarning(ServiceStrings.Log_NoMatchFiveMinutes);

        if (_credentialBudget.IsExhausted)
            log.LogWarning(ServiceStrings.Log_CredentialRetriesStillPaused,
                MaxCredentialAttempts);
    }
}

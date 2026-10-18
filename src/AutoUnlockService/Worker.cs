using AutoUnlockService.Ble;
using AutoUnlockService.Config;
using AutoUnlockService.Core;
using AutoUnlockService.Credentials;
using AutoUnlockService.Management;
using AutoUnlockService.Notifications;
using AutoUnlockService.Session;

namespace AutoUnlockService;

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
    private readonly object _gate = new();
    private ProximityStateMachine _machine = null!;
    private DateTimeOffset _lastSummary;
    private long _lastMatchTicks;

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
            log.LogInformation("启动时屏幕{State}", sessionLocked ? "已锁定" : "未锁定");
        }
        else
        {
            log.LogWarning("无法确定启动时屏幕是否已锁，按未锁定处理");
        }

        _machine = new ProximityStateMachine(time, current.Proximity, current.Notification, lockedAtStart);
        telemetry.OnStateChange(_machine.State);   // 遥测：初始状态没有转换事件，先行播种
        _lastSummary = time.GetUtcNow();
        Volatile.Write(ref _lastMatchTicks, time.GetUtcNow().UtcTicks);

        if (current.Notification.Mode == NotifierMode.Dialog)
            notifier.Degrade("配置指定使用对话框模式");

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
            await source.StartAsync(ct);

            // La prevalidacion es la primera mitad del doble seguro
            bool credentialsOk = validator.Validate(current.Credentials.UserName, current.Credentials.Password);
            Transition(() => _machine.OnCredentialsChanged(credentialsOk));

            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(EvaluateInterval, time, ct); }
                catch (OperationCanceledException) { break; }

                Transition(() => _machine.Evaluate(_history));
                WriteSummaryIfDue();
            }
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
        _history.Add(sample.Rssi, sample.Timestamp);
        telemetry.OnSample(sample.Timestamp.ToUnixTimeMilliseconds(), sample.Rssi);
        Volatile.Write(ref _lastMatchTicks, time.GetUtcNow().UtcTicks);
    }

    private void OnStatus(string status)
    {
        log.LogInformation("BLE 状态：{Status}", status);
        telemetry.OnScanStatus(status);
    }

    private void OnHelperConnected(int sessionId) => telemetry.OnHelperConnection(true);

    private void OnHelperDisconnected() => telemetry.OnHelperConnection(false);

    private void OnCredConnected() => telemetry.OnCredConnection(true);

    private void OnCredDisconnected() => telemetry.OnCredConnection(false);

    private void OnResumed()
    {
        // El reloj puede haber saltado horas: el historico ya no representa nada
        log.LogInformation("系统已从睡眠中恢复，清空 RSSI 历史并重新进入宽限期");
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
                _history.Reset();
                telemetry.OnHistoryReset();   // 遥测：设备变更，历史作废，管理端据 gen 变化清空曲线
                _credentialBudget.Reset();
                log.LogInformation("目标设备已变更，解析缓存与 RSSI 历史已清空");
            }

            if (change.CredentialsChanged)
            {
                bool ok = validator.Validate(
                    change.Options.Credentials.UserName, change.Options.Credentials.Password);
                if (ok) _credentialBudget.Reset();
                Transition(() => _machine.OnCredentialsChanged(ok));
            }
        }
        catch (Exception ex)
        {
            // Corre en el hilo del temporizador del OptionsGate: una excepcion sin
            // capturar aqui mataria el proceso
            log.LogError(ex, "应用配置变更时出错");
        }
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

        log.LogInformation("状态 {From} → {To}：{Reason}", change.From, change.To, change.Reason);
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
                    await notifier.NotifyAsync(change.Reason + "，请修改 appsettings.json 后自动恢复",
                        CancellationToken.None);
                    break;

                case ProximityAction.NotifySuspended:
                    await notifier.NotifyAsync(change.Reason + "，请修改 appsettings.json 后自动恢复",
                        CancellationToken.None);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // CancelAsync retira el aviso pendiente y su PromptAsync termina asi:
            // es un desenlace normal ("la senal volvio"), no una averia.
            // No hay respuesta de usuario que entregar a la maquina de estados
            log.LogDebug("动作 {Action} 的提示已被撤回（正常情况）", change.Action);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "执行动作 {Action} 时出错", change.Action);
        }
    }

    private async Task TrySendCredentialsAsync()
    {
        if (_credentialBudget.IsExhausted)
        {
            // El estado LockedArmed seguira pidiendo el envio cada segundo: solo interesa
            // en Debug; el aviso visible lo da el resumen periodico
            log.LogDebug("凭据重试预算已耗尽，跳过本次推送");
            return;
        }

        var c = options.Current.Credentials;
        if (await credentialPipe.SendCredentialsAsync(c.UserName, c.Password, CancellationToken.None)
            && _credentialBudget.NoteAttempt())
        {
            log.LogWarning(
                "连续 {Count} 次推送凭据均未收到认证结果，为避免账户锁定，已暂停自动解锁重试；手动解锁或修改凭据后恢复",
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
            "摘要：状态 {State}{Batch}，最近 30 秒 {Count} 个读数，平均 {Average:0.0} dBm，距上次匹配 {Since:0} 秒",
            _machine.State, batch.Length == 0 ? "" : $"（{batch}）", stats.Count,
            stats.Count == 0 ? double.NaN : stats.Average, sinceMatch);

        if (sinceMatch > TimeSpan.FromMinutes(5).TotalSeconds)
            log.LogWarning("已有 5 分钟未匹配到目标设备，请检查 IRK 配置或设备是否在范围内");

        if (_credentialBudget.IsExhausted)
            log.LogWarning("自动解锁重试仍处于暂停状态（连续 {Count} 次推送凭据未收到认证结果）；手动解锁或修改凭据后恢复",
                MaxCredentialAttempts);
    }
}

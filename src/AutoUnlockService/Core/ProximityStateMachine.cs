using AutoUnlockService.Config;
using AutoUnlockService.Credentials;

namespace AutoUnlockService.Core;

public enum ProximityState
{
    Unlocked,
    PendingLock,
    Snoozed,
    LockedWaitingDeparture,
    LockedArmed,
    UnlockPending,
    Suspended
}

public enum ProximityAction
{
    None,
    ShowLockPrompt,
    CancelLockPrompt,
    LockScreen,
    SendCredentials,
    NotifySuspended,
    // Suspension desde PendingLock: hay que retirar el aviso Y notificar la suspension
    CancelLockPromptAndNotifySuspended
}

public enum PromptResponse { Reject, Snooze, Timeout, Dismissed }

public enum SessionEvent { Lock, Unlock }

public sealed record StateChange(
    ProximityState From, ProximityState To, ProximityAction Action, string Reason);

public sealed record AuthResult(bool Success, uint NtStatus);

// Toda la politica de bloqueo y desbloqueo vive aqui. No toca Windows ni la red:
// devuelve la accion que el orquestador debe ejecutar
public sealed class ProximityStateMachine
{
    private readonly TimeProvider _time;
    private DateTimeOffset _graceUntil;
    private DateTimeOffset _snoozeUntil;
    private DateTimeOffset _promptDeadline;
    // Se recuerda incluso en Suspended, para saber a que estado volver al reanudar
    private bool _screenLocked;
    private DateTimeOffset _unlockSentAt;

    public ProximityStateMachine(
        TimeProvider time,
        ProximityOptions proximity,
        NotificationOptions notification,
        bool screenLockedAtStartup)
    {
        _time = time;
        Proximity = proximity;
        Notification = notification;
        _screenLocked = screenLockedAtStartup;
        _graceUntil = time.GetUtcNow() + TimeSpan.FromSeconds(proximity.StartupGraceSeconds);
        State = screenLockedAtStartup
            ? ProximityState.LockedWaitingDeparture
            : ProximityState.Unlocked;
    }

    // Mutables: la recarga en caliente las sustituye sin reconstruir la maquina
    public ProximityOptions Proximity { get; set; }
    public NotificationOptions Notification { get; set; }

    public ProximityState State { get; private set; }

    public StateChange? Evaluate(RssiHistory history)
    {
        var now = _time.GetUtcNow();
        return State switch
        {
            ProximityState.Unlocked    => EvaluateUnlocked(history, now),
            ProximityState.PendingLock => EvaluatePendingLock(history, now),
            ProximityState.Snoozed     => EvaluateSnoozed(now),
            ProximityState.LockedWaitingDeparture => EvaluateWaitingDeparture(history, now),
            ProximityState.LockedArmed            => EvaluateArmed(history, now),
            ProximityState.UnlockPending          => EvaluateUnlockPending(now),
            // Suspended no reacciona a las muestras: solo lo saca un cambio de configuracion
            _ => null
        };
    }

    private StateChange? EvaluateUnlocked(RssiHistory history, DateTimeOffset now)
    {
        if (now < _graceUntil) return null;

        // Nunca bloqueamos si el dispositivo objetivo no ha aparecido ni una sola vez:
        // recien instalado, "sin muestras" significa "sin configurar", no "el usuario se fue"
        if (!history.HasEverSeenDevice) return null;

        var (far, reason) = IsFar(history, now);
        if (!far) return null;

        _promptDeadline = now + TimeSpan.FromSeconds(Notification.PromptTimeoutSeconds);
        return Transition(ProximityState.PendingLock, ProximityAction.ShowLockPrompt, reason);
    }

    private StateChange? EvaluatePendingLock(RssiHistory history, DateTimeOffset now)
    {
        var (near, nearReason) = IsNear(history, now);
        if (near)
            return Transition(ProximityState.Unlocked, ProximityAction.CancelLockPrompt,
                $"信号恢复：{nearReason}");

        // Red de seguridad: aunque la respuesta del aviso nunca llegue, el plazo vence igual
        if (now >= _promptDeadline)
        {
            _screenLocked = true;
            return Transition(ProximityState.LockedWaitingDeparture, ProximityAction.LockScreen,
                "通知超时无响应");
        }

        return null;
    }

    private StateChange? EvaluateSnoozed(DateTimeOffset now) =>
        now >= _snoozeUntil
            ? Transition(ProximityState.Unlocked, ProximityAction.None, "延期结束")
            : null;

    private StateChange? EvaluateWaitingDeparture(RssiHistory history, DateTimeOffset now)
    {
        // Aqui NUNCA se envian credenciales, por fuerte que sea la senal: el usuario que
        // pulsa Win+L sentado debe irse de verdad antes de que se arme el desbloqueo
        // La gracia de arranque/reanudacion tambien cuenta aqui: tras vaciar el historico,
        // una ventana vacia significa "aun sin datos", no "el usuario se fue". Sin esto,
        // Win+L sentado + suspension de pantalla + despertar desbloquearia sin haberse ido
        if (now < _graceUntil) return null;

        var (far, reason) = IsFar(history, now);
        return far
            ? Transition(ProximityState.LockedArmed, ProximityAction.None, $"已确认离开：{reason}")
            : null;
    }

    private StateChange? EvaluateArmed(RssiHistory history, DateTimeOffset now)
    {
        var (near, reason) = IsNear(history, now);
        if (!near) return null;

        _unlockSentAt = now;
        return Transition(ProximityState.UnlockPending, ProximityAction.SendCredentials,
            $"已确认靠近：{reason}");
    }

    private StateChange? EvaluateUnlockPending(DateTimeOffset now)
    {
        // ReportResult no esta garantizado: si LogonUI se destruye, el resultado nunca llega.
        // Agotado el plazo volvemos a armar y reintentamos; esto NO es motivo para parar
        var timeout = TimeSpan.FromSeconds(Proximity.UnlockPendingTimeoutSeconds);
        return now - _unlockSentAt >= timeout
            ? Transition(ProximityState.LockedArmed, ProximityAction.None, "等待认证结果超时，将重试")
            : null;
    }

    public StateChange? OnPromptResponse(PromptResponse response)
    {
        if (State != ProximityState.PendingLock) return null;
        var now = _time.GetUtcNow();

        switch (response)
        {
            case PromptResponse.Reject:
                _snoozeUntil = now + TimeSpan.FromSeconds(Notification.RejectSnoozeSeconds);
                return Transition(ProximityState.Snoozed, ProximityAction.None,
                    $"用户拒绝锁屏，{Notification.RejectSnoozeSeconds:0} 秒内不再提示");

            case PromptResponse.Snooze:
                _snoozeUntil = now + TimeSpan.FromMinutes(Notification.SnoozeMinutes);
                return Transition(ProximityState.Snoozed, ProximityAction.None,
                    $"用户延期 {Notification.SnoozeMinutes:0} 分钟");

            default:
                _screenLocked = true;
                return Transition(ProximityState.LockedWaitingDeparture, ProximityAction.LockScreen,
                    response == PromptResponse.Timeout ? "通知超时无响应" : "用户关闭了通知");
        }
    }

    public StateChange? OnSessionEvent(SessionEvent e)
    {
        // Suspended es el unico estado que ignora los eventos de sesion: si el usuario
        // entra escribiendo la clave a mano, eso no significa que la clave del fichero
        // haya dejado de estar mal
        if (State == ProximityState.Suspended)
        {
            _screenLocked = e == SessionEvent.Lock;
            return null;
        }

        if (e == SessionEvent.Lock)
        {
            _screenLocked = true;
            if (State == ProximityState.LockedWaitingDeparture) return null;

            var action = State == ProximityState.PendingLock
                ? ProximityAction.CancelLockPrompt
                : ProximityAction.None;
            return Transition(ProximityState.LockedWaitingDeparture, action, "屏幕已锁定");
        }

        _screenLocked = false;
        if (State == ProximityState.Unlocked) return null;

        // Si el aviso seguia en pantalla hay que retirarlo, o quedaria con botones activos
        var unlockAction = State == ProximityState.PendingLock
            ? ProximityAction.CancelLockPrompt
            : ProximityAction.None;
        return Transition(ProximityState.Unlocked, unlockAction, "屏幕已解锁");
    }

    public StateChange? OnAuthResult(AuthResult result)
    {
        if (State != ProximityState.UnlockPending) return null;

        if (result.Success)
        {
            _screenLocked = false;
            return Transition(ProximityState.Unlocked, ProximityAction.None, "自动解锁成功");
        }

        if (NtStatusPolicy.ShouldSuspend(result.NtStatus))
            return Transition(ProximityState.Suspended, ProximityAction.NotifySuspended,
                $"自动解锁已暂停：{NtStatusPolicy.Describe(result.NtStatus)}");

        return Transition(ProximityState.LockedArmed, ProximityAction.None,
            $"解锁失败将重试：{NtStatusPolicy.Describe(result.NtStatus)}");
    }

    // La llama el orquestador cuando cambian usuario o clave en el fichero,
    // con el resultado de la prevalidacion LogonUser
    public StateChange? OnCredentialsChanged(bool preValidationPassed)
    {
        if (!preValidationPassed)
        {
            if (State == ProximityState.Suspended) return null;

            // Desde PendingLock el aviso sigue en pantalla: se retira como en toda otra salida
            var action = State == ProximityState.PendingLock
                ? ProximityAction.CancelLockPromptAndNotifySuspended
                : ProximityAction.NotifySuspended;
            return Transition(ProximityState.Suspended, action, "凭据预验证失败，自动解锁已暂停");
        }

        if (State != ProximityState.Suspended) return null;

        var target = _screenLocked
            ? ProximityState.LockedWaitingDeparture
            : ProximityState.Unlocked;
        return Transition(target, ProximityAction.None, "凭据已更新且验证通过，自动解锁恢复");
    }

    // Tras despertar de suspension el reloj puede haber saltado horas. El orquestador
    // vacia el historico y aqui se vuelve a conceder el periodo de gracia, para no
    // bloquear la pantalla en el instante de abrir la tapa
    public StateChange? OnResume()
    {
        var proximity = Proximity;
        _graceUntil = _time.GetUtcNow() + TimeSpan.FromSeconds(proximity.StartupGraceSeconds);

        // PendingLock no consulta la gracia: el aviso que el usuario nunca vio esta caducado
        // y su plazo ya vencio, asi que se retira y se reevalua desde cero
        return State == ProximityState.PendingLock
            ? Transition(ProximityState.Unlocked, ProximityAction.CancelLockPrompt, "系统从睡眠恢复，旧的锁屏提示已作废")
            : null;
    }

    private (bool Far, string Reason) IsFar(RssiHistory history, DateTimeOffset now)
    {
        // Copia local: la recarga en caliente puede sustituir Proximity en mitad de la evaluacion
        var proximity = Proximity;
        var window = TimeSpan.FromSeconds(proximity.LockWindowSeconds);
        var stats = history.InWindow(now, window);

        if (stats.Count == 0)
            return (true, $"{proximity.LockWindowSeconds:0} 秒内无信号");

        if (stats.Count < proximity.MinSamples) return (false, "");

        return stats.Average < proximity.LockThresholdDbm
            ? (true, $"{proximity.LockWindowSeconds:0} 秒内平均 {stats.Average:0.0} dBm" +
                     $"（{stats.Count} 个读数）< {proximity.LockThresholdDbm:0}")
            : (false, "");
    }

    private (bool Near, string Reason) IsNear(RssiHistory history, DateTimeOffset now)
    {
        // Copia local: la recarga en caliente puede sustituir Proximity en mitad de la evaluacion
        var proximity = Proximity;
        var window = TimeSpan.FromSeconds(proximity.UnlockWindowSeconds);
        var stats = history.InWindow(now, window);

        // Sin señal nunca es "cerca": el desbloqueo jamas se basa en una suposicion
        if (stats.Count < proximity.MinSamples) return (false, "");

        return stats.Average > proximity.UnlockThresholdDbm
            ? (true, $"{proximity.UnlockWindowSeconds:0} 秒内平均 {stats.Average:0.0} dBm" +
                     $"（{stats.Count} 个读数）> {proximity.UnlockThresholdDbm:0}")
            : (false, "");
    }

    private StateChange Transition(ProximityState to, ProximityAction action, string reason)
    {
        var change = new StateChange(State, to, action, reason);
        State = to;
        return change;
    }
}

using BleUnlockService.Config;
using BleUnlockService.Credentials;
using BleUnlockService.Resources;

namespace BleUnlockService.Core;

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

// Substatus: el subestado que da LogonUI (p. ej. C000006A = clave incorrecta dentro de C000006D)
public sealed record AuthResult(bool Success, uint NtStatus, uint Substatus = 0);

// Lote en curso: Leave = confirmar salida (antes del aviso o con la pantalla ya bloqueada),
// LockConfirm = confirmar el bloqueo tras el aviso, Unlock = confirmar la llegada
public enum BatchKind { Leave, LockConfirm, Unlock }

public sealed record BatchProgress(BatchKind Kind, int Count, int Target);

// Toda la politica de bloqueo y desbloqueo vive aqui. No toca Windows ni la red:
// devuelve la accion que el orquestador debe ejecutar.
//
// 判定改版（2026-10-18，三次修订）：批次全数达标（触发读数不计入，不取平均）。
//   - 触发：低于锁定阈值的读数；
//   - 触发后收满 N 个读数，且每一个都达到阈值才算成立；任一条不达标整批作废，等下一个触发重来；
//   - 静默注入只在锁定侧，且只在两种情况下启用：停播前最后一条读数已低于阈值（信号先变弱
//     再消失 = 真离开的形状，2026-11-04 起要求连续 2 个弱读数），或完全无读数达到兜底时长
//     （NoSignalDepartureSeconds，默认 180 s，0 = 关闭；设备关机/被拿走/跑出范围）。
//     信号还强时的停播只是设备省电打盹，不算离开——这是 10/18 实测「坐在电脑前也弹窗」的根源；
//   - 弹窗不再自动超时：锁定只由「弹窗后新收的一批」或用户点击决定。
public sealed class ProximityStateMachine
{
    // 静默 = 离开证据。宽限内不注入：BLE 偶发丢包不算离开
    internal static readonly TimeSpan SilenceGrace = TimeSpan.FromSeconds(3);
    // 停播前至少要有这么多个连续的弱读数，停播才算「信号先变弱再消失」（2026-11-04：单个偶发
    // 低读数紧跟一次省电停播，曾在人坐在电脑前时触发弹窗）
    internal const int FadedReadingsBeforeSilence = 2;
    internal const double SilentValueDbm = -127;

    private readonly TimeProvider _time;
    private DateTimeOffset _graceUntil;
    private DateTimeOffset _snoozeUntil;
    // Se recuerda incluso en Suspended, para saber a que estado volver al reanudar
    private bool _screenLocked;
    private DateTimeOffset _unlockSentAt;
    // Receptor Bluetooth colgado (ScanWatchdog): sin anuncios de nadie, el silencio no es prueba de nada
    private bool _scanStalled;
    // Sin usuario o clave en la configuración: se bloquea al irse, pero el desbloqueo nunca se arma
    private bool _credentialsMissing;

    // —— 锁定侧批次（Unlocked / PendingLock / LockedWaitingDeparture） ——
    private bool _lockBatchActive;
    private int _lockBatchCount;
    private int _lockBatchSilentCount;      // 当前批次里的静默值个数（日志用）
    private DateTimeOffset _lockWatermark;  // 已消费到的读数时间（批次水位线）
    private DateTimeOffset _lockSilenceFrom; // 静默计时基准：最近一条读数或进入当前状态的时刻
    private int _lockSilentInjected;        // 本轮静默已注入的个数（出现新读数时清零）
    private short? _lastRealRssi;           // 最后一条真实读数（静默补点门槛的依据）
    private int _weakStreak;                // 连续低于锁定阈值的真实读数个数（遇到不低于阈值的即清零）
    private DateTimeOffset _lastRealAt;     // 上一条真实读数的时间（超长兜底计时；状态转换不重置）

    // —— 解锁侧批次（PendingLock 的信号恢复判定 / LockedArmed） ——
    private bool _unlockBatchActive;
    private int _unlockBatchCount;
    private DateTimeOffset _unlockWatermark;
    private DateTimeOffset _unlockLastValueAt; // 批次内上一个值（或触发）的时间：间隙过大时旧值作废

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
        _lastRealAt = time.GetUtcNow();
        State = screenLockedAtStartup
            ? ProximityState.LockedWaitingDeparture
            : ProximityState.Unlocked;
        ResetSides(State);
    }

    // Mutables: la recarga en caliente las sustituye sin reconstruir la maquina
    public ProximityOptions Proximity { get; set; }
    public NotificationOptions Notification { get; set; }

    public ProximityState State { get; private set; }

    // Progreso del lote actual (lectura aproximada, solo para observar: resumen y telemetría)
    public BatchProgress? Progress => State switch
    {
        ProximityState.PendingLock when _lockBatchActive =>
            new BatchProgress(BatchKind.LockConfirm, _lockBatchCount, Math.Max(1, Proximity.LockConfirmSampleCount)),
        ProximityState.LockedArmed when _unlockBatchActive =>
            new BatchProgress(BatchKind.Unlock, _unlockBatchCount, Math.Max(1, Proximity.UnlockSampleCount)),
        ProximityState.Unlocked or ProximityState.LockedWaitingDeparture when _lockBatchActive =>
            new BatchProgress(BatchKind.Leave, _lockBatchCount, Math.Max(1, Proximity.LockPromptSampleCount)),
        _ => null
    };

    // 摘要日志用：当前批次的收集进度
    public string BatchProgressText => Progress switch
    {
        { Kind: BatchKind.LockConfirm } p => string.Format(ServiceStrings.Batch_LockConfirm, p.Count, p.Target),
        { Kind: BatchKind.Unlock } p => string.Format(ServiceStrings.Batch_Unlock, p.Count, p.Target),
        { } p => string.Format(ServiceStrings.Batch_Leave, p.Count, p.Target),
        null => ""
    };

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

        if (LockBatchConfirmed(history, now, Proximity.LockPromptSampleCount, out var reason))
            return Transition(ProximityState.PendingLock, ProximityAction.ShowLockPrompt,
                string.Format(ServiceStrings.Reason_DepartureDetected, reason));

        return null;
    }

    private StateChange? EvaluatePendingLock(RssiHistory history, DateTimeOffset now)
    {
        // Primero el regreso: si el usuario vuelve, el aviso se retira y no se bloquea. Basta con
        // superar el umbral de BLOQUEO (no el de desbloqueo): con la pantalla aún abierta, salir de
        // la zona de «lejos» ya desmiente la salida (decisión del usuario, 2026-10-31)
        // Sin límite de hueco entre lecturas (2026-11-04): un reloj que anuncia cada 6–15 s nunca juntaba
        // 3 lecturas a ≤ 3 s y el aviso no se retiraba con el usuario al lado. Retirar el aviso solo
        // retrasa el bloqueo, nunca desbloquea: aquí la evidencia antigua no es un riesgo
        if (UnlockBatchNear(history, Proximity.UnlockSampleCount, Proximity.LockThresholdDbm, enforceGap: false, out var nearReason))
            return Transition(ProximityState.Unlocked, ProximityAction.CancelLockPrompt,
                string.Format(ServiceStrings.Reason_SignalRecovered, nearReason));

        // 弹窗后新收的一批（弹窗前的读数不参与）：全部低于阈值才锁屏。
        // 等待期没有任何自动超时：只有这批读数或用户点击能决定
        if (LockBatchConfirmed(history, now, Proximity.LockConfirmSampleCount, out var reason))
        {
            _screenLocked = true;
            return Transition(ProximityState.LockedWaitingDeparture, ProximityAction.LockScreen,
                string.Format(ServiceStrings.Reason_DepartureConfirmedAfterPrompt, reason));
        }

        return null;
    }

    private StateChange? EvaluateSnoozed(DateTimeOffset now) =>
        now >= _snoozeUntil
            ? Transition(ProximityState.Unlocked, ProximityAction.None, ServiceStrings.Reason_SnoozeEnded)
            : null;

    private StateChange? EvaluateWaitingDeparture(RssiHistory history, DateTimeOffset now)
    {
        // Aqui NUNCA se envian credenciales, por fuerte que sea la senal: el usuario que
        // pulsa Win+L sentado debe irse de verdad antes de que se arme el desbloqueo
        // La gracia de arranque/reanudacion tambien cuenta aqui: tras vaciar el historico,
        // una ausencia de datos significa "aun sin datos", no "el usuario se fue". Sin esto,
        // Win+L sentado + suspension de pantalla + despertar desbloquearia sin haberse ido
        if (now < _graceUntil) return null;

        // Sin credenciales no hay nada que armar: se espera aquí hasta que el usuario entre a mano
        if (_credentialsMissing) return null;

        if (LockBatchConfirmed(history, now, Proximity.LockPromptSampleCount, out var reason))
            return Transition(ProximityState.LockedArmed, ProximityAction.None, string.Format(ServiceStrings.Reason_DepartureConfirmed, reason));

        return null;
    }

    private StateChange? EvaluateArmed(RssiHistory history, DateTimeOffset now)
    {
        if (!UnlockBatchNear(history, Proximity.UnlockSampleCount, Proximity.UnlockThresholdDbm, enforceGap: true, out var reason)) return null;

        _unlockSentAt = now;
        return Transition(ProximityState.UnlockPending, ProximityAction.SendCredentials,
            string.Format(ServiceStrings.Reason_ApproachConfirmed, reason));
    }

    private StateChange? EvaluateUnlockPending(DateTimeOffset now)
    {
        // ReportResult no esta garantizado: si LogonUI se destruye, el resultado nunca llega.
        // Agotado el plazo volvemos a armar y reintentamos; esto NO es motivo para parar
        var timeout = TimeSpan.FromSeconds(Proximity.UnlockPendingTimeoutSeconds);
        return now - _unlockSentAt >= timeout
            ? Transition(ProximityState.LockedArmed, ProximityAction.None, ServiceStrings.Reason_AuthTimeoutRetry)
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
                    string.Format(ServiceStrings.Reason_UserRejected, Notification.RejectSnoozeSeconds));

            case PromptResponse.Snooze:
                _snoozeUntil = now + TimeSpan.FromMinutes(Notification.SnoozeMinutes);
                return Transition(ProximityState.Snoozed, ProximityAction.None,
                    string.Format(ServiceStrings.Reason_UserSnoozed, Notification.SnoozeMinutes));

            case PromptResponse.Dismissed:
                // Cerrar la X es una orden explicita del usuario: bloquear ya
                _screenLocked = true;
                return Transition(ProximityState.LockedWaitingDeparture, ProximityAction.LockScreen,
                    ServiceStrings.Reason_UserDismissed);

            default:
                // 改版：超时不再锁屏（弹窗本身也不会自动超时；这里只剩降级对话框的自身期限）。
                // 锁定永远由「弹窗后的读数」或用户点击决定
                return null;
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
            return Transition(ProximityState.LockedWaitingDeparture, action, ServiceStrings.Reason_ScreenLocked);
        }

        _screenLocked = false;
        if (State == ProximityState.Unlocked) return null;

        // Si el aviso seguia en pantalla hay que retirarlo, o quedaria con botones activos
        var unlockAction = State == ProximityState.PendingLock
            ? ProximityAction.CancelLockPrompt
            : ProximityAction.None;
        return Transition(ProximityState.Unlocked, unlockAction, ServiceStrings.Reason_ScreenUnlocked);
    }

    public StateChange? OnAuthResult(AuthResult result)
    {
        if (State != ProximityState.UnlockPending) return null;

        if (result.Success)
        {
            _screenLocked = false;
            return Transition(ProximityState.Unlocked, ProximityAction.None, ServiceStrings.Reason_BleUnlockSucceeded);
        }

        if (NtStatusPolicy.ShouldSuspend(result.NtStatus))
            return Transition(ProximityState.Suspended, ProximityAction.NotifySuspended,
                string.Format(ServiceStrings.Reason_BleUnlockSuspended, NtStatusPolicy.Describe(result.NtStatus, result.Substatus)));

        return Transition(ProximityState.LockedArmed, ProximityAction.None,
            string.Format(ServiceStrings.Reason_UnlockFailedRetry, NtStatusPolicy.Describe(result.NtStatus, result.Substatus)));
    }

    // La llama el orquestador cuando cambian usuario o clave en el fichero,
    // con el resultado de la prevalidacion LogonUser. failureReason sustituye al motivo generico
    // cuando se sabe algo mas (p. ej. que la cuenta no existe en este equipo)
    public StateChange? OnCredentialsChanged(bool preValidationPassed, string? failureReason = null)
    {
        _credentialsMissing = false;

        if (!preValidationPassed)
        {
            if (State == ProximityState.Suspended) return null;

            // Desde PendingLock el aviso sigue en pantalla: se retira como en toda otra salida
            var action = State == ProximityState.PendingLock
                ? ProximityAction.CancelLockPromptAndNotifySuspended
                : ProximityAction.NotifySuspended;
            return Transition(ProximityState.Suspended, action, failureReason ?? ServiceStrings.Reason_CredentialPrecheckFailed);
        }

        if (State != ProximityState.Suspended) return null;

        var target = _screenLocked
            ? ProximityState.LockedWaitingDeparture
            : ProximityState.Unlocked;
        return Transition(target, ProximityAction.None, ServiceStrings.Reason_CredentialsRestored);
    }

    // La configuración no tiene usuario o clave (equipo recién instalado, o el usuario aún no la ha
    // escrito). No es un fallo como el de una clave incorrecta: el bloqueo al irse sigue funcionando y solo
    // se renuncia al desbloqueo automático. Un desbloqueo armado o en curso se abandona
    public StateChange? OnCredentialsMissing()
    {
        _credentialsMissing = true;

        switch (State)
        {
            case ProximityState.Suspended:
                return Transition(
                    _screenLocked ? ProximityState.LockedWaitingDeparture : ProximityState.Unlocked,
                    ProximityAction.None, ServiceStrings.Reason_CredentialsMissingLockOnly);
            case ProximityState.LockedArmed or ProximityState.UnlockPending:
                return Transition(ProximityState.LockedWaitingDeparture, ProximityAction.None,
                    ServiceStrings.Reason_CredentialsMissingLockOnly);
            default:
                return null;
        }
    }

    // Tras despertar de suspension el reloj puede haber saltado horas. El orquestador
    // vacia el historico y aqui se vuelve a conceder el periodo de gracia, para no
    // bloquear la pantalla en el instante de abrir la tapa
    public StateChange? OnResume()
    {
        var proximity = Proximity;
        _graceUntil = _time.GetUtcNow() + TimeSpan.FromSeconds(proximity.StartupGraceSeconds);

        // 睡眠期间的时间不算「静默」：历史已清空，信号知识归零，旧读数不能当作补点依据
        _lastRealRssi = null;
        _weakStreak = 0;
        _lastRealAt = _time.GetUtcNow();

        // PendingLock no consulta la gracia: el aviso que el usuario nunca vio esta caducado
        // y ya no tiene sentido mantenerlo, asi que se retira y se reevalua desde cero
        if (State == ProximityState.PendingLock)
            return Transition(ProximityState.Unlocked, ProximityAction.CancelLockPrompt,
                ServiceStrings.Reason_ResumeDiscardsPrompt);

        // El historico se vacia en el orquestador: los lotes a medias ya no valen
        ResetSides(State);
        return null;
    }

    // Receptor colgado: el silencio deja de contar como salida (ni para avisar o bloquear, ni para armar
    // el desbloqueo). Lo deducido en los segundos previos a detectarlo tampoco vale: el aviso se retira
    // y un desbloqueo armado vuelve a exigir la salida. Al recuperarse, el silencio se cuenta desde
    // cero, igual que tras una suspensión
    public StateChange? OnScanHealthChanged(bool stalled)
    {
        if (stalled == _scanStalled) return null;
        _scanStalled = stalled;

        if (!stalled)
        {
            _lastRealRssi = null;
            _weakStreak = 0;
            _lastRealAt = _time.GetUtcNow();
            ResetSides(State);
            return null;
        }

        switch (State)
        {
            case ProximityState.PendingLock:
                return Transition(ProximityState.Unlocked, ProximityAction.CancelLockPrompt,
                    ServiceStrings.Reason_ScanStalledDiscardsPrompt);
            case ProximityState.LockedArmed:
                return Transition(ProximityState.LockedWaitingDeparture, ProximityAction.None,
                    ServiceStrings.Reason_ScanStalledDisarms);
            default:
                ResetSides(State);
                return null;
        }
    }

    // —— 批次判定 ——

    // 锁定侧：消费新读数（第一个低于阈值的只做触发、不计入）；静默超过宽限后
    // 每 1 秒注入一个极低值（静默本身即触发，注入值计入）。触发后收满 size 个
    // 且每一个都低于阈值 → true；任一条不达标整批作废，等下一个触发重来
    private bool LockBatchConfirmed(RssiHistory history, DateTimeOffset now, int target, out string reason)
    {
        reason = "";
        // Copia local: la recarga en caliente puede sustituir Proximity en mitad de la evaluacion
        var threshold = Proximity.LockThresholdDbm;
        var size = Math.Max(1, target);

        foreach (var (at, rssi) in history.Since(_lockWatermark))
        {
            _lockWatermark = at;
            _lockSilenceFrom = at;      // 有读数：静默计时重新开始
            _lockSilentInjected = 0;
            _lastRealRssi = rssi;       // 静默补点门槛与兜底都依据「最后一条真实读数」
            _lastRealAt = at;
            _weakStreak = rssi < threshold ? _weakStreak + 1 : 0;

            if (!_lockBatchActive)
            {
                if (rssi >= threshold) continue;
                StartLockBatch();       // 触发读数不计入
                continue;
            }

            if (rssi >= threshold)
            {
                ResetLockBatch();       // 全数达标：一条不达标即整批作废
                continue;
            }

            _lockBatchCount++;
            if (_lockBatchCount >= size)
            {
                reason = DescribeLockBatch(size, threshold);
                return true;
            }
        }

        // 静默：没有读数，但不等于「离开」。设备（手表/手机）会周期性停播省电：
        // 信号还强时的静默不是证据，注入会把打盹误判成离开——10/18 实测误弹的根源。
        // 只有两种情况补点：信号先变弱（最后一条读数已低于阈值），或完全无读数达到兜底时长。
        if (_scanStalled) return false;

        var silence = now - _lockSilenceFrom;
        if (silence <= SilenceGrace) return false;

        // 变弱要有趋势（连续多个弱读数），一个偶发低值不算；兜底时长可配置，0 = 关闭兜底
        var signalFaded = _lastRealRssi is { } last && last < threshold && _weakStreak >= FadedReadingsBeforeSilence;
        var fallbackSeconds = Proximity.NoSignalDepartureSeconds;
        var longSilence = fallbackSeconds > 0 && now - _lastRealAt >= TimeSpan.FromSeconds(fallbackSeconds);
        if (!signalFaded && !longSilence) return false;

        var due = (int)Math.Floor((silence - SilenceGrace).TotalSeconds);
        while (_lockSilentInjected < due)
        {
            _lockSilentInjected++;
            if (!_lockBatchActive) StartLockBatch();

            _lockBatchCount++;
            _lockBatchSilentCount++;
            if (_lockBatchCount >= size)
            {
                reason = DescribeLockBatch(size, threshold);
                return true;
            }
        }

        return false;
    }

    // 解锁侧：第一个高于阈值的读数只做触发、不计入；之后每个读数都必须高于阈值，
    // 收满 size 个才算靠近。间隙超过静默宽限的旧值作废：宁可重新数，不拿陈旧证据解锁。
    // threshold：LockedArmed 用解锁阈值；PendingLock 撤回提示用锁定阈值。
    // enforceGap：只有解锁才限制相邻读数间隔；撤回提示不限制（见 EvaluatePendingLock）
    private bool UnlockBatchNear(RssiHistory history, int target, double threshold, bool enforceGap, out string reason)
    {
        reason = "";
        var size = Math.Max(1, target);

        foreach (var (at, rssi) in history.Since(_unlockWatermark))
        {
            _unlockWatermark = at;

            if (enforceGap && _unlockBatchActive && at - _unlockLastValueAt > SilenceGrace)
                ResetUnlockBatch();

            if (!_unlockBatchActive)
            {
                if (rssi <= threshold) continue;
                _unlockBatchActive = true;
                _unlockBatchCount = 0;
                _unlockLastValueAt = at;   // 触发读数不计入
                continue;
            }

            if (rssi <= threshold)
            {
                ResetUnlockBatch();        // 全数达标：一条不达标即整批作废
                continue;
            }

            _unlockBatchCount++;
            _unlockLastValueAt = at;
            if (_unlockBatchCount >= size)
            {
                reason = string.Format(ServiceStrings.Reason_AllAbove, size, threshold);
                return true;
            }
        }

        return false;
    }

    private void StartLockBatch()
    {
        _lockBatchActive = true;
        _lockBatchCount = 0;
        _lockBatchSilentCount = 0;
    }

    private void ResetLockBatch()
    {
        _lockBatchActive = false;
        _lockBatchCount = 0;
        _lockBatchSilentCount = 0;
    }

    private void ResetUnlockBatch()
    {
        _unlockBatchActive = false;
        _unlockBatchCount = 0;
    }

    private string DescribeLockBatch(int count, double threshold) =>
        _lockBatchSilentCount > 0
            ? string.Format(ServiceStrings.Reason_AllBelowWithSilent, count, threshold, _lockBatchSilentCount)
            : string.Format(ServiceStrings.Reason_AllBelow, count, threshold);

    // 每次转换都从零开始：批次属于状态，跨状态不继承任何收集。
    // 例外：进入 PendingLock 立即启动「弹窗后」的确认批次（不需要触发读数）
    private void ResetSides(ProximityState state)
    {
        var now = _time.GetUtcNow();

        ResetLockBatch();
        _lockWatermark = now;
        _lockSilenceFrom = now;
        _lockSilentInjected = 0;

        ResetUnlockBatch();
        _unlockWatermark = now;

        if (state == ProximityState.PendingLock)
            _lockBatchActive = true;
    }

    private StateChange Transition(ProximityState to, ProximityAction action, string reason)
    {
        var change = new StateChange(State, to, action, reason);
        State = to;
        ResetSides(to);
        return change;
    }
}

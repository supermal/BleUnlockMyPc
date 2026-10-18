using System.Globalization;
using System.Runtime.InteropServices;
using AutoUnlockService.Session;

namespace AutoUnlockService.Notifications;

// Implementacion sin ayudante: WTSSendMessage esta pensado justo para esto,
// un servicio que pregunta algo en la sesion del usuario.
// No la afecta el modo "no molestar", pero el dialogo no se puede cerrar desde fuera.
// 判定改版：没有自动锁屏期限——对话框自身的期限只决定它何时消失，超时不再锁屏
public sealed class WtsMessageNotifier(ILogger<WtsMessageNotifier> log) : INotifier
{
    private const uint MbYesNo = 0x00000004;
    private const uint MbIconExclamation = 0x00000030;
    private const uint MbSystemModal = 0x00001000;
    public const uint NoticeTimeoutSeconds = 60;
    private const uint IdYes = 6;
    private const uint IdNo = 7;

    // WTSSendMessage interpreta 0 como "esperar para siempre": nunca se debe enviar 0.
    // El dialogo degradado no puede quedarse eternamente frente a la sesion: su propia
    // vida util es el tope del protocolo (10 分钟)
    private static readonly TimeSpan DialogLifetime = TimeSpan.FromSeconds(HelperProtocol.MaxTimeoutSeconds);

    // Se acota a [1, MaxTimeoutSeconds] y se redondea hacia arriba
    public static uint ClampTimeoutSeconds(TimeSpan timeout)
    {
        var seconds = Math.Ceiling(timeout.TotalSeconds);
        if (!double.IsFinite(seconds) || seconds < 1) return 1;
        return (uint)Math.Min(seconds, HelperProtocol.MaxTimeoutSeconds);
    }

    public Task<PromptOutcome> PromptAsync(double snoozeMinutes, CancellationToken ct)
    {
        var sessionId = WtsNative.GetActiveConsoleSessionId();
        if (sessionId == WtsNative.InvalidSession)
        {
            log.LogWarning("没有活动的控制台会话，无法弹出提示");
            return Task.FromResult(PromptOutcome.Timeout);
        }

        const string title = "AutoUnlock";
        var message = string.Format(CultureInfo.InvariantCulture,
            "未检测到你的设备。若持续未检测到，屏幕将自动锁定。\n\n" +
            "「是」= 本次不锁屏\n" +
            "「否」= {0:0} 分钟内不锁屏",
            snoozeMinutes);

        return Task.Run(() =>
        {
            bool ok = WtsNative.WTSSendMessage(
                WtsNative.CurrentServer, sessionId,
                title, (uint)(title.Length * 2),
                message, (uint)(message.Length * 2),
                MbYesNo | MbIconExclamation | MbSystemModal,
                ClampTimeoutSeconds(DialogLifetime),
                out uint response,
                bWait: true);

            if (!ok)
            {
                // Sin dialogo el usuario no ha sido avisado; la nueva logica no bloquea por
                // tiempo, asi que aqui solo se deja constancia
                log.LogError("WTSSendMessage 调用失败（错误码 {Error}），用户未收到任何提示",
                    Marshal.GetLastWin32Error());
                return PromptOutcome.Timeout;
            }

            return response switch
            {
                IdYes => PromptOutcome.Reject,
                IdNo  => PromptOutcome.Snooze,
                // El dialogo se acabo solo: no es una respuesta del usuario ni motivo de bloqueo
                _     => PromptOutcome.Timeout
            };
        }, ct);
    }

    // El dialogo clasico no se puede retirar desde fuera: esta limitacion es conocida
    // y esta documentada en el diseño
    public Task CancelAsync() => Task.CompletedTask;

    public Task NotifyAsync(string message, CancellationToken ct)
    {
        var sessionId = WtsNative.GetActiveConsoleSessionId();
        if (sessionId == WtsNative.InvalidSession) return Task.CompletedTask;

        const string title = "AutoUnlock";
        const uint mbOk = 0x00000000;
        // Plazo acotado: con 0 el dialogo no caduca nunca y los avisos se acumularian
        bool ok = WtsNative.WTSSendMessage(
            WtsNative.CurrentServer, sessionId,
            title, (uint)(title.Length * 2),
            message, (uint)(message.Length * 2),
            mbOk | MbIconExclamation, NoticeTimeoutSeconds, out _, bWait: false);
        if (!ok)
            log.LogError("WTSSendMessage 调用失败（错误码 {Error}），用户未收到通知：{Message}",
                Marshal.GetLastWin32Error(), message);

        return Task.CompletedTask;
    }
}

using System.Globalization;
using AutoUnlockService.Session;
using CommunityToolkit.WinUI.Notifications;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace AutoUnlockHelper;

// Envoltorio del Toast con dos botones. El proceso sigue vivo, asi que los eventos
// Activated y Dismissed llegan en proceso y no hace falta registrar un activador COM.
// ToastNotificationManagerCompat se encarga del AUMID y del acceso directo del menu
// Inicio, que Windows exige a las aplicaciones sin empaquetar.
//
// Garantia central: por cada Prompt se emite como maximo UNA respuesta, sea cual sea
// el orden en que lleguen boton, descarte y plazo (todos pasan por Answer)
internal sealed class ToastPrompt : IDisposable
{
    private readonly ToastNotifierCompat _notifier = ToastNotificationManagerCompat.CreateToastNotifier();
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private ToastNotification? _current;
    private ITimer? _timer;
    private int _currentId;
    private bool _answered = true;   // sin Toast activo no hay nada que responder

    public ToastPrompt(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public event Action<int, PromptOutcome>? Responded;

    public void Show(int id, double timeoutSeconds, double snoozeMinutes)
    {
        var xml = new XmlDocument();
        xml.LoadXml(string.Format(CultureInfo.InvariantCulture, """
            <toast scenario="reminder" launch="ignore">
              <visual>
                <binding template="ToastGeneric">
                  <text>即将锁定屏幕</text>
                  <text>未检测到你的设备，{0:0} 秒后将自动锁屏。</text>
                </binding>
              </visual>
              <actions>
                <action content="拒绝" arguments="reject" activationType="foreground"/>
                <action content="{1:0} 分钟内不锁定" arguments="snooze" activationType="foreground"/>
              </actions>
            </toast>
            """, timeoutSeconds, snoozeMinutes));

        var toast = new ToastNotification(xml) { Tag = "autounlock-lock-prompt" };
        toast.Activated += OnActivated;
        toast.Dismissed += OnDismissed;

        lock (_lock)
        {
            // Un Prompt nuevo sustituye al anterior sin responderlo
            RetractLocked();
            _current = toast;
            _currentId = id;
            _answered = false;
            // El ayudante vigila tambien el plazo; el servicio tiene ademas el suyo
            _timer = _time.CreateTimer(_ => Answer(toast, PromptOutcome.Timeout),
                null, TimeSpan.FromSeconds(timeoutSeconds), Timeout.InfiniteTimeSpan);
        }

        _notifier.Show(toast);
    }

    // CANCEL del servicio: se retira el Toast y no se responde nada
    public void Cancel(int id)
    {
        lock (_lock)
        {
            if (_current is null || _currentId != id) return;
            RetractLocked();
        }
    }

    // Aviso sin botones, usado al entrar en estado parado
    public void Notify(string message)
    {
        var xml = new XmlDocument();
        xml.LoadXml($"""
            <toast launch="ignore">
              <visual>
                <binding template="ToastGeneric">
                  <text>AutoUnlock</text>
                  <text>{System.Security.SecurityElement.Escape(message)}</text>
                </binding>
              </visual>
            </toast>
            """);
        _notifier.Show(new ToastNotification(xml));
    }

    public void Dispose()
    {
        lock (_lock) RetractLocked();
    }

    private void OnActivated(ToastNotification sender, object args)
    {
        var arguments = (args as ToastActivatedEventArgs)?.Arguments ?? "";
        var outcome = arguments switch
        {
            "reject" => PromptOutcome.Reject,
            "snooze" => PromptOutcome.Snooze,
            _ => PromptOutcome.Dismissed   // clic en el cuerpo del Toast
        };
        Answer(sender, outcome);
    }

    private void OnDismissed(ToastNotification sender, ToastDismissedEventArgs args)
    {
        // ApplicationHidden es nuestra propia retirada: no es respuesta del usuario
        if (args.Reason == ToastDismissalReason.ApplicationHidden) return;
        // TimedOut aqui es el plazo del propio Windows para el Toast, no el nuestro:
        // para el servicio equivale a que el usuario lo descarto
        Answer(sender, PromptOutcome.Dismissed);
    }

    // Unico camino hacia Responded. 'source' evita que un evento tardio de un Toast
    // ya sustituido conteste al Prompt nuevo
    private void Answer(ToastNotification source, PromptOutcome outcome)
    {
        int id;
        lock (_lock)
        {
            if (_answered || !ReferenceEquals(source, _current)) return;
            _answered = true;
            id = _currentId;
            RetractLocked();
        }
        Responded?.Invoke(id, outcome);
    }

    private void RetractLocked()
    {
        _timer?.Dispose();
        _timer = null;
        _answered = true;
        if (_current is null) return;

        var toast = _current;
        _current = null;
        toast.Activated -= OnActivated;
        toast.Dismissed -= OnDismissed;
        try { _notifier.Hide(toast); } catch { /* ya no estaba */ }
    }
}

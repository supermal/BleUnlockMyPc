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
// el orden en que lleguen boton y descarte (todos pasan por Answer).
// 判定改版：没有倒计时、没有自动响应；Toast 常驻直到用户点击或服务撤回
internal sealed class ToastPrompt : IDisposable
{
    private readonly ToastNotifierCompat _notifier = ToastNotificationManagerCompat.CreateToastNotifier();
    private readonly object _lock = new();
    private ToastNotification? _current;
    private int _currentId;
    private bool _answered = true;   // sin Toast activo no hay nada que responder

    public event Action<int, PromptOutcome>? Responded;

    public void Show(int id, double snoozeMinutes)
    {
        // El servicio ya paso al Prompt nuevo: el anterior se retira (sin responderlo) antes de
        // construir nada, para que un fallo al montar el nuevo no deje el viejo activo
        lock (_lock) RetractLocked();

        var xml = new XmlDocument();
        xml.LoadXml(string.Format(CultureInfo.InvariantCulture, """
            <toast scenario="reminder" launch="ignore">
              <visual>
                <binding template="ToastGeneric">
                  <text>即将锁定屏幕</text>
                  <text>未检测到你的设备。若持续未检测到，屏幕将自动锁定。</text>
                </binding>
              </visual>
              <actions>
                <action content="拒绝" arguments="reject" activationType="foreground"/>
                <action content="{0:0} 分钟内不锁定" arguments="snooze" activationType="foreground"/>
              </actions>
            </toast>
            """, snoozeMinutes));

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
        }

        try
        {
            _notifier.Show(toast);
        }
        catch
        {
            // Si el Toast no se pudo mostrar no queda Prompt pendiente
            lock (_lock)
            {
                if (ReferenceEquals(_current, toast)) RetractLocked();
            }
            throw;
        }
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
        // ApplicationHidden es nuestra propia retirada: no es respuesta del usuario.
        // Cualquier otro motivo (la X del usuario o una caducidad del propio Windows)
        // cuenta como descarte explicito: el servicio lo trata como "bloquear ya"
        if (args.Reason == ToastDismissalReason.ApplicationHidden) return;
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
        _answered = true;
        if (_current is null) return;

        var toast = _current;
        _current = null;
        toast.Activated -= OnActivated;
        toast.Dismissed -= OnDismissed;
        try { _notifier.Hide(toast); } catch { /* ya no estaba */ }
    }
}

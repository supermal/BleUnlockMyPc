using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BleUnlockService.Session;

// Puente entre la sesion 0 del servicio y la sesion interactiva del usuario
public static class WtsNative
{
    public const uint InvalidSession = 0xFFFFFFFF;

    public static uint GetActiveConsoleSessionId() => WTSGetActiveConsoleSessionId();

    public static bool TryQueryUserToken(uint sessionId, out SafeAccessTokenHandle token)
    {
        if (WTSQueryUserToken(sessionId, out token)) return true;
        token.Dispose();
        return false;
    }

    // WTS_INFO_CLASS.WTSSessionInfoEx
    private const int SessionInfoExClass = 25;

    // WTSINFOEXW: Level @0, relleno de alineacion @4-7 (los LARGE_INTEGER del union
    // fuerzan alineacion de 8), SessionId @8, SessionState @12, SessionFlags @16.
    // Verificado contra el documento oficial de WTSINFOEX_LEVEL1_W y con sonda real
    // en Windows 11 (build 26200): sesion desbloqueada -> Level=1, SessionId=1,
    // SessionState=0 (WTSActive), SessionFlags=1
    private const int SessionFlagsOffset = 16;
    private const int InfoExHeaderSize = SessionFlagsOffset + sizeof(int);

    // El defecto de inversion de Windows 7 (LOCK/UNLOCK intercambiados) no aplica:
    // en la sonda de Windows 11 una sesion desbloqueada devuelve UNLOCK (1)
    // null = WTS_SESSIONSTATE_UNKNOWN (-1) o valor no documentado
    internal static bool? MapSessionLockFlags(long sessionFlags) => sessionFlags switch
    {
        0 => true,    // WTS_SESSIONSTATE_LOCK
        1 => false,   // WTS_SESSIONSTATE_UNLOCK
        _ => null
    };

    // Devuelve true solo si puede confirmarse el estado de la sesion de consola activa.
    // Sesion inexistente, Level inesperado, error de consulta o valor desconocido: false.
    // La direccion segura ante duda es "desbloqueada": suponer "bloqueada" con la
    // pantalla desbloqueada dejaria de bloquear al usuario alejarse
    public static bool TryIsActiveConsoleSessionLocked(out bool locked)
    {
        locked = false;
        var sessionId = GetActiveConsoleSessionId();
        if (sessionId == InvalidSession) return false;

        if (!WTSQuerySessionInformationW(CurrentServer, sessionId, SessionInfoExClass,
                out var buffer, out var bytes)
            || buffer == IntPtr.Zero || bytes < InfoExHeaderSize)
        {
            return false;
        }

        try
        {
            if (Marshal.ReadInt32(buffer, 0) != 1) return false;   // Level distinto de WTSINFOEX_LEVEL1

            var mapped = MapSessionLockFlags(Marshal.ReadInt32(buffer, SessionFlagsOffset));
            if (mapped is null) return false;

            locked = mapped.Value;
            return true;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle phToken);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "WTSSendMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSSendMessage(
        IntPtr hServer, uint sessionId,
        string pTitle, uint titleLength,
        string pMessage, uint messageLength,
        uint style, uint timeoutSeconds,
        out uint pResponse, [MarshalAs(UnmanagedType.Bool)] bool bWait);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true,
        EntryPoint = "WTSQuerySessionInformationW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(
        IntPtr hServer, uint sessionId, int wtsInfoClass, out IntPtr ppBuffer, out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    internal static readonly IntPtr CurrentServer = IntPtr.Zero;
}

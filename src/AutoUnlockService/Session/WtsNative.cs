using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AutoUnlockService.Session;

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

    internal static readonly IntPtr CurrentServer = IntPtr.Zero;
}

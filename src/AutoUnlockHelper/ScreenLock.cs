using System.Runtime.InteropServices;

namespace AutoUnlockHelper;

// El ayudante vive en la sesion del usuario, asi que puede bloquear directamente
internal static class ScreenLock
{
    public static bool Lock() => LockWorkStation();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();
}

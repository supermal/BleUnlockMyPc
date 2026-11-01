using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using BleUnlockService.Resources;

namespace BleUnlockService.Session;

// Resultado de intentar arrancar un proceso en la sesion del usuario. NoUserSession no es un
// fallo del arranque: simplemente aun no hay nadie con la sesion iniciada (p. ej. al arrancar el equipo)
public enum UserSessionStartResult { Started, NoUserSession, Failed }

// Un servicio en la sesion 0 no puede crear ventanas ni bloquear la pantalla:
// todo eso se ejecuta con el token del usuario de la sesion interactiva
public static class UserSessionProcess
{
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const int ErrorNoToken = 1008;

    public static int? StartInActiveSession(string commandLine, ILogger log) =>
        TryStartInActiveSession(commandLine, log, out var pid) == UserSessionStartResult.Started ? pid : null;

    public static UserSessionStartResult TryStartInActiveSession(string commandLine, ILogger log, out int processId)
    {
        processId = 0;
        var sessionId = WtsNative.GetActiveConsoleSessionId();
        if (sessionId == WtsNative.InvalidSession)
        {
            log.LogWarning(ServiceStrings.Log_NoConsoleSessionForProcess);
            return UserSessionStartResult.NoUserSession;
        }

        bool gotToken = WtsNative.TryQueryUserToken(sessionId, out var token);
        int tokenError = gotToken ? 0 : Marshal.GetLastWin32Error();
        if (!gotToken)
        {
            // Sin usuario con sesion iniciada (pantalla de inicio de sesion) no hay token que pedir
            log.LogWarning(ServiceStrings.Log_UserTokenFailed, tokenError);
            return tokenError == ErrorNoToken ? UserSessionStartResult.NoUserSession : UserSessionStartResult.Failed;
        }

        using (token)
        {
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };

            bool ok = CreateProcessAsUserW(
                token, null, commandLine,
                IntPtr.Zero, IntPtr.Zero, false,
                CreateUnicodeEnvironment | CreateNoWindow,
                IntPtr.Zero, null, ref si, out var pi);

            if (!ok)
            {
                log.LogWarning(ServiceStrings.Log_CreateProcessAsUserFailed, Marshal.GetLastWin32Error());
                return UserSessionStartResult.Failed;
            }

            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            processId = pi.dwProcessId;
            return UserSessionStartResult.Started;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUserW(
        SafeAccessTokenHandle hToken, string? lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}

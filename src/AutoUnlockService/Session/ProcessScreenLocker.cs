using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AutoUnlockService.Session;

// Un servicio en la sesion 0 no puede llamar a LockWorkStation: hay que ejecutarlo
// dentro de la sesion del usuario con su propio token
public sealed class ProcessScreenLocker(ILogger<ProcessScreenLocker> log) : IScreenLocker
{
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;

    public Task<bool> LockAsync(CancellationToken ct) => Task.FromResult(Lock());

    private bool Lock()
    {
        var sessionId = WtsNative.GetActiveConsoleSessionId();
        if (sessionId == WtsNative.InvalidSession)
        {
            log.LogWarning("没有活动的控制台会话，无法锁屏");
            return false;
        }

        if (!WtsNative.TryQueryUserToken(sessionId, out var token))
        {
            log.LogWarning("获取用户令牌失败，错误码 {Error}，无法锁屏", Marshal.GetLastWin32Error());
            return false;
        }

        using (token)
        {
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            // Ruta entre comillas: CreateProcessAsUser analiza la linea de comandos y una ruta con espacios la rompe
            var command = $"\"{Environment.SystemDirectory}\\rundll32.exe\" user32.dll,LockWorkStation";

            bool ok = CreateProcessAsUserW(
                token, null, command,
                IntPtr.Zero, IntPtr.Zero, false,
                CreateUnicodeEnvironment | CreateNoWindow,
                IntPtr.Zero, null, ref si, out var pi);

            if (!ok)
            {
                log.LogWarning("CreateProcessAsUser 失败，错误码 {Error}", Marshal.GetLastWin32Error());
                return false;
            }

            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            log.LogInformation("已在会话 {Session} 中执行锁屏", sessionId);
            return true;
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

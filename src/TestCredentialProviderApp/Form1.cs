using Microsoft.Win32;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace TestCredentialProviderApp;

public partial class Form1 : Form
{
    private const string CpRegistryKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{6D9E3F7A-2C8B-4E5D-A1F0-3B7C9D2E6F4A}";

    private readonly CancellationTokenSource _pipeCts = new();

    public Form1()
    {
        InitializeComponent();

        Text        = "AutoUnlock 测试工具";
        label1.Text = "用户名";
        label2.Text = "密码";
        label3.Text = "AutoUnlock 凭据提供程序测试";
        btnLock.Text = "锁屏";

        UpdateInstallButton();
        _ = RunPipeServerAsync(_pipeCts.Token);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _pipeCts.Cancel();
        _pipeCts.Dispose();
        base.OnFormClosed(e);
    }

    // ── Named Pipe 服务器（模拟 AutoUnlockService）────────────────────────

    private async Task RunPipeServerAsync(CancellationToken ct)
    {
        Log("管道服务器已启动，监听 \\\\.\\pipe\\AutoUnlock");
        Log("提示：运行本工具前请确保 AutoUnlockService 已停止");

        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    "AutoUnlock",
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(ct);
                Log("凭据提供程序已连接到管道");

                string username = string.Empty, password = string.Empty;
                Invoke(() =>
                {
                    username = txtUserName.Text.Trim();
                    password = txtPassword.Text;
                });

                if (string.IsNullOrEmpty(username))
                {
                    Log("⚠ 用户名为空，已拒绝连接，请先填写凭据");
                    pipe.Disconnect();
                    continue;
                }

                byte[] payload = Encoding.UTF8.GetBytes($"{username}\n{password}\n");
                await pipe.WriteAsync(payload, ct);
                Log($"已发送凭据：{username} / {"*".PadRight(password.Length, '*')}");

                byte[] buf = new byte[16];
                int read = await pipe.ReadAsync(buf, ct);
                string response = Encoding.UTF8.GetString(buf, 0, read).Trim();
                Log($"收到响应：{response}");

                pipe.Disconnect();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log($"管道错误：{ex.Message}，2 秒后重试…");
                try { await Task.Delay(2000, ct); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                pipe?.Dispose();
            }
        }

        Log("管道服务器已停止");
    }

    // ── 按钮事件 ─────────────────────────────────────────────────────────

    private void btnLock_Click(object sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(txtUserName.Text))
        {
            MessageBox.Show(this, "请先填写用户名和密码，再锁屏。",
                "未填写凭据", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Log("执行锁屏，等待凭据提供程序连接…");
        NativeMethods.LockWorkStation();
    }

    private void btnInstall_Click(object sender, EventArgs e)
    {
        if (!IsRunningAsAdministrator())
        {
            MessageBox.Show(this,
                "安装需要管理员权限。\n\n请右键本程序，选择「以管理员身份运行」。",
                "需要管理员权限", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var sourceDll = LocateProviderDll();
        if (sourceDll is null)
        {
            MessageBox.Show(this,
                "未找到 AutoUnlockCredentialProvider.dll。\n\n" +
                "请先在 Visual Studio 中以 Release|x64 编译 AutoUnlockCredentialProvider 项目。",
                "未找到 DLL", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var destDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AutoUnlock");
        var targetDll = Path.Combine(destDir, "AutoUnlockCP.dll");

        if (MessageBox.Show(this,
                $"即将安装并注册凭据提供程序：\n\n来源：{sourceDll}\n目标：{targetDll}\n\n继续？",
                "确认安装", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        try
        {
            Directory.CreateDirectory(destDir);
            File.Copy(sourceDll, targetDll, overwrite: true);
            Log($"DLL 已复制到 {targetDll}");

            var proc = Process.Start(new ProcessStartInfo
            {
                FileName        = "regsvr32.exe",
                Arguments       = $"/s \"{targetDll}\"",
                UseShellExecute = false,
                CreateNoWindow  = true
            });
            proc?.WaitForExit();

            if (proc is null || proc.ExitCode != 0)
                throw new InvalidOperationException($"regsvr32 退出码：{proc?.ExitCode}");

            Log("Credential Provider 注册成功 ✓");
            UpdateInstallButton();
            MessageBox.Show(this,
                "安装完成！\n\n测试步骤：\n1. 填写 Windows 用户名和密码\n2. 点击【锁屏】\n3. 等待自动解锁",
                "安装完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log($"安装失败：{ex.Message}");
            MessageBox.Show(this, $"安装失败：{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ── 辅助方法 ─────────────────────────────────────────────────────────

    private void UpdateInstallButton()
    {
        bool registered = IsProviderRegistered();
        btnInstall.Visible = true;
        btnInstall.Text    = registered ? "重新安装 CP" : "安装 Credential Provider";
        Log(registered
            ? "检测到 Credential Provider 已注册 ✓"
            : "Credential Provider 未注册，请点击安装按钮");
    }

    private static bool IsProviderRegistered()
    {
        using var key = Registry.LocalMachine.OpenSubKey(CpRegistryKey);
        return key is not null;
    }

    private static string? LocateProviderDll()
    {
        // 1. exe 同目录
        var bundled = Path.Combine(AppContext.BaseDirectory, "AutoUnlockCredentialProvider.dll");
        if (File.Exists(bundled)) return bundled;

        // 2. 向上找到 .sln，再定位 x64\Release 输出目录
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.GetFiles(dir, "*.sln").Length > 0)
            {
                var candidate = Path.Combine(dir, "x64", "Release", "AutoUnlockCredentialProvider.dll");
                if (File.Exists(candidate)) return candidate;
                break;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }

        return null;
    }

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void Log(string msg)
    {
        string line = $"{DateTime.Now:HH:mm:ss}  {msg}";
        if (txtLog.InvokeRequired)
            txtLog.Invoke(() => txtLog.AppendText(line + Environment.NewLine));
        else
            txtLog.AppendText(line + Environment.NewLine);
    }
}

public static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    internal static extern bool LockWorkStation();
}

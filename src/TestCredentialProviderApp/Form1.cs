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

    private CancellationTokenSource _pipeCts = new();
    private DateTime _credSentAt = DateTime.MinValue;
    private const int CooldownSeconds = 30;

    public Form1()
    {
        InitializeComponent();

        Text         = "AutoUnlock 测试工具";
        label1.Text  = "用户名";
        label2.Text  = "密码";
        label3.Text  = "AutoUnlock 凭据提供程序测试";
        btnLock.Text = "锁屏（触发式）";

        UpdateInstallButton();
        _ = RunListenerAsync(_pipeCts.Token);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _pipeCts.Cancel();
        _pipeCts.Dispose();
        base.OnFormClosed(e);
    }

    // ════════════════════════════════════════════════════════════════════════
    // 监听式（模拟 Windows Service 的运行方式）
    //
    // Windows Service 必须使用此方式：
    //   - Service 启动时即开始监听管道
    //   - CP 是客户端，锁屏出现时主动来连接
    //   - 当 RSSI 检测到用户靠近，Service 通过已建立的管道立即推送凭据
    //
    // 测试时注意：此模式下 CP 一旦连接就会立即收到凭据并解锁，
    // 相当于模拟「Service 已决定现在应该解锁」的状态。
    // ════════════════════════════════════════════════════════════════════════

    private async Task RunListenerAsync(CancellationToken ct)
    {
        Log("[监听式] 管道服务器已启动，持续等待 CP 连接…");

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

                var cooldownLeft = CooldownSeconds - (DateTime.Now - _credSentAt).TotalSeconds;
                if (cooldownLeft > 0)
                {
                    Log($"[监听式] 冷却中，还需等待 {cooldownLeft:F0} 秒（防止密码错误时死循环）");
                    pipe.Disconnect();
                    continue;
                }

                Log("[监听式] CP 已连接，立即发送凭据（模拟 RSSI 已触发）");
                await SendCredentialsAsync(pipe, ct);
                pipe.Disconnect();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log($"[监听式] 管道错误：{ex.Message}，2 秒后重试…");
                try { await Task.Delay(2000, ct); }
                catch (OperationCanceledException) { break; }
            }
            finally { pipe?.Dispose(); }
        }

        Log("[监听式] 已停止");
    }

    // ════════════════════════════════════════════════════════════════════════
    // 触发式（测试工具专用的一次性完整流程）
    //
    // 点击按钮后主动发起并控制整个流程：
    //   锁屏 → 等待 CP 连接 → 倒计时 → 发送凭据
    //
    // 与监听式的区别：
    //   - 每次测试是一次性操作，不影响正常锁屏
    //   - 可以精确控制「何时」发送（倒计时）
    //   - 触发期间监听式暂停，结束后自动恢复
    // ════════════════════════════════════════════════════════════════════════

    private async Task TriggerLockAndUnlockAsync(int delaySeconds)
    {
        // 1. 暂停监听式，避免同名管道冲突
        _pipeCts.Cancel();
        _pipeCts.Dispose();
        await Task.Delay(300);
        Log("[触发式] 监听式已暂停");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        NamedPipeServerStream? pipe = null;
        try
        {
            // 2. 先建好管道再锁屏，确保 CP 连接时服务端已就绪
            pipe = new NamedPipeServerStream(
                "AutoUnlock",
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            Log("[触发式] 一次性管道已就绪，执行锁屏…");
            NativeMethods.LockWorkStation();

            // 3. 等待 CP 连接（60 秒超时）
            await pipe.WaitForConnectionAsync(cts.Token);
            Log("[触发式] CP 已连接");

            // 4. 倒计时
            await CountdownAsync(delaySeconds, cts.Token);

            // 5. 发送凭据
            await SendCredentialsAsync(pipe, cts.Token);
            pipe.Disconnect();
        }
        catch (OperationCanceledException)
        {
            Log("[触发式] 已超时或取消");
        }
        catch (Exception ex)
        {
            Log($"[触发式] 错误：{ex.Message}");
        }
        finally
        {
            pipe?.Dispose();

            // 6. 恢复监听式
            _pipeCts = new CancellationTokenSource();
            _ = RunListenerAsync(_pipeCts.Token);
            Log("[触发式] 监听式已恢复");
        }
    }

    // ── 共用：发送凭据并读取响应 ──────────────────────────────────────────

    private async Task SendCredentialsAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        string username = string.Empty, password = string.Empty;
        Invoke(() =>
        {
            username = txtUserName.Text.Trim();
            password = txtPassword.Text;
        });

        if (string.IsNullOrEmpty(username))
        {
            Log("⚠ 用户名为空，已跳过发送");
            return;
        }

        byte[] payload = Encoding.UTF8.GetBytes($"CREDS\n{username}\n{password}\n");
        await pipe.WriteAsync(payload, ct);
        _credSentAt = DateTime.Now;
        Log($"已发送凭据：{username} / {"*".PadRight(password.Length, '*')}");

        // El CP responde ACK enseguida y RESULT cuando Windows ha resuelto la autenticacion
        var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        string? ack = await reader.ReadLineAsync(ct);
        Log($"收到确认：{ack}");

        // El CP no escribe nada si ReportResult nunca llega: se acota la espera
        using var resultCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        resultCts.CancelAfter(TimeSpan.FromSeconds(30));
        string? result;
        try { result = await reader.ReadLineAsync(resultCts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log("等待认证结果超时（30 秒），CP 未回报结果");
            return;
        }
        Log(result switch
        {
            "RESULT OK"                       => "✓ 认证成功",
            not null when result.StartsWith("RESULT FAIL ") =>
                $"✗ 认证失败，NTSTATUS 0x{result["RESULT FAIL ".Length..]}",
            null                              => "连接已断开，未收到认证结果",
            _                                 => $"收到未知响应：{result}"
        });
    }

    private async Task CountdownAsync(int seconds, CancellationToken ct)
    {
        for (int i = seconds; i > 0; i--)
        {
            Log($"倒计时 {i}…");
            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    // ── 按钮事件 ──────────────────────────────────────────────────────────

    private void btnLock_Click(object sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(txtUserName.Text))
        {
            MessageBox.Show(this, "请先填写用户名和密码，再锁屏。",
                "未填写凭据", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        // 点击按钮即视为用户确认本次凭据有效，重置冷却
        _credSentAt = DateTime.MinValue;
        _ = TriggerLockAndUnlockAsync(5);
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
                "安装完成！\n\n测试步骤：\n1. 填写 Windows 用户名和密码\n2. 点击【锁屏（触发式）】\n3. 等待自动解锁",
                "安装完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log($"安装失败：{ex.Message}");
            MessageBox.Show(this, $"安装失败：{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ── 辅助方法 ──────────────────────────────────────────────────────────

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
        var bundled = Path.Combine(AppContext.BaseDirectory, "AutoUnlockCredentialProvider.dll");
        if (File.Exists(bundled)) return bundled;

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

using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoUnlockService;

public sealed class Worker : BackgroundService
{
    private readonly ServiceSettings _settings;
    private readonly ILogger<Worker> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly PipeServer _pipeServer;
    private readonly CredentialStore _credentialStore;

    public Worker(
        IOptions<ServiceSettings> settings,
        ILogger<Worker> logger,
        ILoggerFactory loggerFactory,
        PipeServer pipeServer,
        CredentialStore credentialStore)
    {
        _settings = settings.Value;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _pipeServer = pipeServer;
        _credentialStore = credentialStore;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AutoUnlockService starting on {Port}", _settings.ComPort);

        var smoother = new RssiSmoother(_settings.SmoothingWindow);
        var stateMachine = new ProximityStateMachine(
            _settings.NearThreshold, _settings.FarThreshold,
            _settings.UnlockConfirmCount, _settings.LockConfirmCount);

        stateMachine.UnlockRequested += () =>
        {
            _logger.LogInformation("UNLOCK triggered by proximity");
            var creds = _credentialStore.Read();
            if (creds is null)
            {
                _logger.LogError("No credentials in Credential Manager for target '{Target}'",
                    _settings.CredentialTarget);
                return;
            }
            _ = _pipeServer.SendCredentialsAsync(creds.Value.username, creds.Value.password, stoppingToken);
        };

        stateMachine.LockRequested += () =>
        {
            _logger.LogInformation("LOCK triggered by proximity");
            LockActiveSession();
        };

        using var monitor = new RssiMonitor(
            _settings.ComPort, _settings.BaudRate,
            _loggerFactory.CreateLogger<RssiMonitor>());

        monitor.RssiReceived += rssi =>
        {
            double smoothed = smoother.Add(rssi);
            stateMachine.Feed(smoothed);
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                monitor.Start();
                _logger.LogInformation("Serial port {Port} opened", _settings.ComPort);
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Serial port error, retrying in 5s");
                monitor.Stop();
                smoother.Reset();
                try { await Task.Delay(5000, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        monitor.Stop();
        _logger.LogInformation("AutoUnlockService stopped");
    }

    private void LockActiveSession()
    {
        uint sessionId = Win32.WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF)
        {
            _logger.LogWarning("No active console session found");
            return;
        }

        if (!Win32.WTSQueryUserToken(sessionId, out var hToken))
        {
            _logger.LogWarning("WTSQueryUserToken failed: {Err}", Marshal.GetLastWin32Error());
            return;
        }

        using (hToken)
        {
            if (!Win32.DuplicateTokenEx(hToken, Win32.TOKEN_ALL_ACCESS, IntPtr.Zero,
                Win32.SecurityImpersonation, Win32.TokenPrimary, out var hDup))
            {
                _logger.LogWarning("DuplicateTokenEx failed: {Err}", Marshal.GetLastWin32Error());
                return;
            }

            using (hDup)
            {
                var si = new Win32.STARTUPINFO
                {
                    cb = Marshal.SizeOf<Win32.STARTUPINFO>(),
                    lpDesktop = "WinSta0\\Default"
                };
                string cmd = "rundll32.exe user32.dll,LockWorkStation";

                if (!Win32.CreateProcessAsUser(hDup, null, cmd,
                    IntPtr.Zero, IntPtr.Zero, false, 0,
                    IntPtr.Zero, null, ref si, out var pi))
                {
                    _logger.LogWarning("CreateProcessAsUser failed: {Err}", Marshal.GetLastWin32Error());
                    return;
                }

                Win32.CloseHandle(pi.hProcess);
                Win32.CloseHandle(pi.hThread);
            }
        }
    }
}

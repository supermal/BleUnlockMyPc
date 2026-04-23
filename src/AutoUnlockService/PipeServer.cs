using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AutoUnlockService;

public sealed class PipeServer : IDisposable
{
    private NamedPipeServerStream? _pipe;
    private readonly ILogger<PipeServer> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    public const string PipeName = "AutoUnlock";

    public PipeServer(ILogger<PipeServer> logger)
    {
        _logger = logger;
    }

    private static NamedPipeServerStream CreatePipe()
    {
        // Para .NET 8 Windows, utilizamos PipeSecurity con el método ACL
        var security = new PipeSecurity();
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        // Agregamos regla de acceso para SYSTEM
        security.AddAccessRule(new PipeAccessRule(
            systemSid,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            4096,
            4096,
            security);
    }

    public async Task SendCredentialsAsync(string username, string password, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    _pipe ??= CreatePipe();

                    if (!_pipe.IsConnected)
                    {
                        _logger.LogInformation("Pipe: waiting for CP client (attempt {Attempt})", attempt);
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        cts.CancelAfter(TimeSpan.FromSeconds(10));
                        await _pipe.WaitForConnectionAsync(cts.Token);
                    }

                    string message = $"{username}\n{password}\n";
                    byte[] data = Encoding.UTF8.GetBytes(message);
                    await _pipe.WriteAsync(data, ct);
                    await _pipe.FlushAsync(ct);

                    byte[] response = new byte[8];
                    int read = await _pipe.ReadAsync(response.AsMemory(0, 8), ct);
                    string resp = Encoding.UTF8.GetString(response, 0, read).Trim();

                    if (resp.Equals("OK", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("Credentials sent successfully");
                        return;
                    }
                    _logger.LogWarning("Unexpected pipe response: '{Resp}'", resp);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Pipe send attempt {Attempt} failed", attempt);
                    ResetPipe();
                    if (attempt < 3) await Task.Delay(500, ct);
                }
            }
            _logger.LogError("Failed to send credentials after 3 attempts");
        }
        finally
        {
            _lock.Release();
        }
    }

    private void ResetPipe()
    {
        _pipe?.Dispose();
        _pipe = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pipe?.Dispose();
        _lock.Dispose();
    }
}

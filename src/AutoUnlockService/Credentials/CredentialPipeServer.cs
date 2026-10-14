using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using AutoUnlockService.Core;

namespace AutoUnlockService.Credentials;

// Servidor de \\.\pipe\AutoUnlock. Mantiene una conexion viva con el CP y empuja
// las credenciales solo cuando la maquina de estados lo pide (nunca al conectar).
// El CP responde ACK enseguida y RESULT cuando Windows resuelve la autenticacion;
// RESULT puede tardar o no llegar jamas, por eso la lectura nunca bloquea nada mas
public sealed class CredentialPipeServer(ILogger<CredentialPipeServer> log, TimeProvider? timeProvider = null)
    : IAsyncDisposable
{
    private const string PipeName = "AutoUnlock";
    private static readonly TimeSpan RebuildDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DisposeJoinTimeout = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile NamedPipeServerStream? _pipe;
    private Task? _loop;
    private CancellationTokenSource? _cts;
    private int _disposed;

    public bool IsClientConnected
    {
        get
        {
            try { return _pipe?.IsConnected == true; }
            catch (ObjectDisposedException) { return false; }
        }
    }

    // Se invoca en el hilo de lectura: el manejador debe ser rapido y no bloquear
    public event Action<AuthResult>? AuthResultReceived;

    public void Start(CancellationToken ct)
    {
        if (_cts is not null || Volatile.Read(ref _disposed) != 0) return;   // un solo arranque

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        _loop = Task.Run(() => AcceptLoopAsync(token), CancellationToken.None);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pipe = CreatePipe();
                _pipe = pipe;
                log.LogInformation("凭据管道已就绪，等待凭据提供程序连接");
                await pipe.WaitForConnectionAsync(ct);
                log.LogInformation("凭据提供程序已连接");

                await ReadLoopAsync(pipe, ct);
                log.LogInformation("凭据提供程序已断开");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogWarning("凭据管道异常：{Message}，稍后重建", ex.Message);
                try { await Task.Delay(RebuildDelay, _time, ct); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                // Cada instancia se libera siempre: ni una por reconexion se queda viva
                var old = Interlocked.Exchange(ref _pipe, null);
                if (old is not null) await old.DisposeAsync();
            }
        }
    }

    private async Task ReadLoopAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, leaveOpen: true);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;   // el CP cerro la conexion

            if (line.Trim() == "ACK")
            {
                log.LogDebug("凭据提供程序已确认收到凭据");
                continue;
            }

            if (CredentialProtocol.TryParseResult(line, out var result))
            {
                log.LogInformation("收到认证结果：成功={Success} NTSTATUS=0x{Status:X8}",
                    result.Success, result.NtStatus);
                try { AuthResultReceived?.Invoke(result); }
                catch (Exception ex)
                {
                    // Un manejador defectuoso no debe costar la conexion
                    log.LogError("处理认证结果的订阅者抛出异常：{Message}", ex.Message);
                }
                continue;
            }

            // No se registra el contenido de la linea
            log.LogWarning("凭据管道收到无法识别的消息");
        }
    }

    public async Task<bool> SendCredentialsAsync(string userName, string password, CancellationToken ct)
    {
        var pipe = _pipe;
        if (pipe is null || !IsClientConnected)
        {
            log.LogWarning("要推送凭据时没有凭据提供程序连接，本次放弃");
            return false;
        }

        byte[] payload;
        try { payload = CredentialProtocol.BuildCreds(userName, password); }
        catch (ArgumentException)
        {
            // Mensaje fijo: no se vuelca ningun valor
            log.LogWarning("凭据包含换行符，无法按协议发送，本次放弃");
            return false;
        }

        try
        {
            await _writeLock.WaitAsync(ct);
            try
            {
                // Plazo de escritura con el reloj inyectado
                using var timeout = new CancellationTokenSource(WriteTimeout, _time);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

                await pipe.WriteAsync(payload, linked.Token);
                await pipe.FlushAsync(linked.Token);
                // Nunca se registra la clave, solo el usuario
                log.LogInformation("已向凭据提供程序推送凭据，用户 {User}", userName);
                return true;
            }
            catch (Exception ex)
            {
                log.LogWarning("推送凭据失败：{Message}", ex.Message);
                // Una escritura a medias deja el flujo desalineado: se descarta la conexion
                // y el CP vuelve a conectar limpio
                try { await pipe.DisposeAsync(); } catch (Exception) { }
                return false;
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;   // cierre concurrente
        }
        finally
        {
            Array.Clear(payload);
        }
    }

    // Solo SYSTEM puede conectarse: el servicio y winlogon/LogonUI corren con esa identidad.
    // La DACL limita a los clientes; no impide que otro proceso elevado actue como servidor
    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 4096,
            outBufferSize: 4096,
            security);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _cts?.Cancel();
        if (_loop is not null)
        {
            // Con plazo: un cierre de servicio nunca debe colgarse esperando al bucle
            try { await _loop.WaitAsync(DisposeJoinTimeout); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                log.LogWarning("凭据管道循环未在期限内退出");
            }
        }

        var pipe = Interlocked.Exchange(ref _pipe, null);
        if (pipe is not null) await pipe.DisposeAsync();
        _cts?.Dispose();
        // _writeLock no se libera: un envio concurrente podria seguir usandolo
    }
}

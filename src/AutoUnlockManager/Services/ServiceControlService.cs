using System.ComponentModel;
using System.ServiceProcess;
using AutoUnlockManager.Core;

namespace AutoUnlockManager.Services;

/// <summary>服务状态分类（托盘图标/菜单与后续页面共用）。</summary>
public enum ServiceStatusKind
{
    /// <summary>查询失败或未知状态。</summary>
    Unknown,

    /// <summary>服务未安装或不可用（Win32 1060）。</summary>
    NotInstalled,

    /// <summary>已停止。</summary>
    Stopped,

    /// <summary>正在启动（过渡态）。</summary>
    StartPending,

    /// <summary>正在运行。</summary>
    Running,

    /// <summary>正在停止（过渡态）。</summary>
    StopPending,
}

/// <summary>服务状态查询结果。Message 仅用于诊断展示，可为空。</summary>
public sealed record ServiceStatusInfo(ServiceStatusKind Kind, string? Message = null);

/// <summary>服务控制操作结果分类。</summary>
public enum ServiceOperationStatus
{
    /// <summary>操作成功（含幂等：目标状态已达成）。</summary>
    Success,

    /// <summary>服务未安装（Win32 1060）。</summary>
    NotInstalled,

    /// <summary>拒绝访问（Win32 5，需要管理员权限）。</summary>
    AccessDenied,

    /// <summary>等待目标状态超时（15 秒）。</summary>
    Timeout,

    /// <summary>其他失败（见 Message）。</summary>
    Failure,
}

/// <summary>服务控制操作结果。Message 为人类可读说明（失败时必填）。</summary>
public sealed record ServiceOperationResult(ServiceOperationStatus Status, string? Message = null)
{
    /// <summary>成功（无额外说明）。</summary>
    public static readonly ServiceOperationResult Success = new(ServiceOperationStatus.Success);
}

/// <summary>
/// SCM 包装（<see cref="ServiceController"/>，服务名 <see cref="ServiceInfo.ServiceName"/>）。
/// 所有操作在后台线程执行、不阻塞 UI；等待目标状态超时 15 秒；异常一律分类为结果，不向上抛。
/// 服务为「自动(延迟)启动」：状态查询不假设已运行。
/// </summary>
public sealed class ServiceControlService
{
    /// <summary>操作超时（等待进入目标状态的最长时间）。</summary>
    public static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(15);

    /// <summary>查询服务当前状态（后台线程；未安装/拒绝访问等分类返回，不抛异常）。</summary>
    public Task<ServiceStatusInfo> GetStatusAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => GetStatusCore(cancellationToken), cancellationToken);

    /// <summary>启动服务并等待进入运行状态。</summary>
    public Task<ServiceOperationResult> StartAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => StartCore(cancellationToken), cancellationToken);

    /// <summary>停止服务并等待进入停止状态。</summary>
    public Task<ServiceOperationResult> StopAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => StopCore(cancellationToken), cancellationToken);

    /// <summary>重启服务：停（等到 Stopped）→ 起（等到 Running）；已停止时等价启动。</summary>
    public Task<ServiceOperationResult> RestartAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => RestartCore(cancellationToken), cancellationToken);

    private static ServiceStatusInfo GetStatusCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var controller = new ServiceController(ServiceInfo.ServiceName);
            return new ServiceStatusInfo(MapStatus(controller.Status));
        }
        catch (Exception ex)
        {
            var code = GetWin32ErrorCode(ex);
            return code switch
            {
                1060 => new ServiceStatusInfo(ServiceStatusKind.NotInstalled, "服务未安装（Win32 1060）"),
                // 拒绝访问等查询失败：灰色「未知」（状态文案「未安装或不可用」仅用于 1060）
                5 => new ServiceStatusInfo(ServiceStatusKind.Unknown, "拒绝访问（Win32 5）"),
                _ => new ServiceStatusInfo(ServiceStatusKind.Unknown, ex.Message),
            };
        }
    }

    private static ServiceOperationResult StartCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var controller = new ServiceController(ServiceInfo.ServiceName);
            var status = controller.Status;
            if (status is ServiceControllerStatus.Running)
            {
                return ServiceOperationResult.Success; // 幂等
            }
            if (status is ServiceControllerStatus.StartPending)
            {
                controller.WaitForStatus(ServiceControllerStatus.Running, OperationTimeout);
                return ServiceOperationResult.Success;
            }
            if (status is ServiceControllerStatus.StopPending)
            {
                // 正在停止：先等停止完成再启动
                controller.WaitForStatus(ServiceControllerStatus.Stopped, OperationTimeout);
            }

            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, OperationTimeout);
            return ServiceOperationResult.Success;
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            return new ServiceOperationResult(ServiceOperationStatus.Timeout, "等待服务进入运行状态超时（15 秒）");
        }
        catch (Exception ex)
        {
            return ClassifyOperationFailure(ex);
        }
    }

    private static ServiceOperationResult StopCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var controller = new ServiceController(ServiceInfo.ServiceName);
            var status = controller.Status;
            if (status is ServiceControllerStatus.Stopped)
            {
                return ServiceOperationResult.Success; // 幂等
            }
            if (status is ServiceControllerStatus.StartPending)
            {
                // 正在启动：先等启动完成再停（Stop 对 StartPending 服务无效）
                controller.WaitForStatus(ServiceControllerStatus.Running, OperationTimeout);
            }
            if (status is not ServiceControllerStatus.StopPending)
            {
                controller.Stop();
            }

            controller.WaitForStatus(ServiceControllerStatus.Stopped, OperationTimeout);
            return ServiceOperationResult.Success;
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            return new ServiceOperationResult(ServiceOperationStatus.Timeout, "等待服务停止超时（15 秒）");
        }
        catch (Exception ex)
        {
            return ClassifyOperationFailure(ex);
        }
    }

    private static ServiceOperationResult RestartCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var controller = new ServiceController(ServiceInfo.ServiceName);
            var status = controller.Status;
            if (status is not ServiceControllerStatus.Stopped)
            {
                if (status is ServiceControllerStatus.StartPending)
                {
                    // 正在启动：先等启动完成，避免对过渡态下发 Stop
                    controller.WaitForStatus(ServiceControllerStatus.Running, OperationTimeout);
                    status = controller.Status;
                }
                if (status is not ServiceControllerStatus.Stopped)
                {
                    if (status is not ServiceControllerStatus.StopPending)
                    {
                        controller.Stop();
                    }
                    controller.WaitForStatus(ServiceControllerStatus.Stopped, OperationTimeout);
                }
            }

            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, OperationTimeout);
            return ServiceOperationResult.Success;
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            return new ServiceOperationResult(ServiceOperationStatus.Timeout, "等待服务完成重启超时（15 秒）");
        }
        catch (Exception ex)
        {
            return ClassifyOperationFailure(ex);
        }
    }

    /// <summary>SCM 状态到分类的映射（Paused 系为防御性映射：自动启动服务不会进入）。</summary>
    private static ServiceStatusKind MapStatus(ServiceControllerStatus status) => status switch
    {
        ServiceControllerStatus.Running => ServiceStatusKind.Running,
        ServiceControllerStatus.StartPending => ServiceStatusKind.StartPending,
        ServiceControllerStatus.StopPending => ServiceStatusKind.StopPending,
        ServiceControllerStatus.Stopped => ServiceStatusKind.Stopped,
        ServiceControllerStatus.Paused => ServiceStatusKind.Stopped,
        ServiceControllerStatus.PausePending => ServiceStatusKind.StopPending,
        ServiceControllerStatus.ContinuePending => ServiceStatusKind.StartPending,
        _ => ServiceStatusKind.Unknown,
    };

    private static ServiceOperationResult ClassifyOperationFailure(Exception ex) => GetWin32ErrorCode(ex) switch
    {
        1060 => new ServiceOperationResult(ServiceOperationStatus.NotInstalled, "服务未安装（Win32 1060）"),
        5 => new ServiceOperationResult(ServiceOperationStatus.AccessDenied, "拒绝访问（Win32 5）：需要管理员权限"),
        1056 => ServiceOperationResult.Success, // ERROR_SERVICE_ALREADY_RUNNING：目标状态已达成
        1062 => ServiceOperationResult.Success, // ERROR_SERVICE_NOT_ACTIVE：目标状态已达成
        _ => new ServiceOperationResult(ServiceOperationStatus.Failure, ex.Message),
    };

    /// <summary>
    /// 提取 Win32 错误码：<see cref="ServiceController"/> 把 SCM 错误包在
    /// InvalidOperationException 的 InnerException（Win32Exception）中。
    /// </summary>
    private static int GetWin32ErrorCode(Exception ex)
    {
        if (ex is Win32Exception win32)
        {
            return win32.NativeErrorCode;
        }
        if (ex.InnerException is Win32Exception inner)
        {
            return inner.NativeErrorCode;
        }
        return 0;
    }
}

namespace AutoUnlockManager.Core;

/// <summary>
/// 服务端固定标识（与 scripts/install.ps1、管理通道协议 v1 保持一致）。
/// </summary>
public static class ServiceInfo
{
    /// <summary>Windows 服务名（SCM 控制与状态查询用）。</summary>
    public const string ServiceName = "AutoUnlockService";

    /// <summary>管理通道命名管道名（完整路径 \\.\pipe\AutoUnlockManagement，设计 §3.1）。</summary>
    public const string ManagementPipeName = "AutoUnlockManagement";
}

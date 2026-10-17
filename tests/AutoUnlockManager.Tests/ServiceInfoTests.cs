using AutoUnlockManager.Core;
using Xunit;

namespace AutoUnlockManager.Tests;

/// <summary>
/// 锁定服务名与管道名的字面值：与 scripts/install.ps1（服务名）和
/// 服务端 ManagementPipeServer（管道名）为跨项目契约，改动需同步三处。
/// </summary>
public class ServiceInfoTests
{
    [Fact]
    public void ServiceName_es_AutoUnlockService()
    {
        Assert.Equal("AutoUnlockService", ServiceInfo.ServiceName);
    }

    [Fact]
    public void ManagementPipeName_es_AutoUnlockManagement()
    {
        Assert.Equal("AutoUnlockManagement", ServiceInfo.ManagementPipeName);
    }
}

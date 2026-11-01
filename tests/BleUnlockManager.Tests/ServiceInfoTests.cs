using BleUnlockManager.Core;
using Xunit;

namespace BleUnlockManager.Tests;

/// <summary>
/// 锁定服务名与管道名的字面值：与 scripts/install.ps1（服务名）和
/// 服务端 ManagementPipeServer（管道名）为跨项目契约，改动需同步三处。
/// </summary>
public class ServiceInfoTests
{
    [Fact]
    public void ServiceName_es_BleUnlockService()
    {
        Assert.Equal("BleUnlockService", ServiceInfo.ServiceName);
    }

    [Fact]
    public void ManagementPipeName_es_BleUnlockManagement()
    {
        Assert.Equal("BleUnlockManagement", ServiceInfo.ManagementPipeName);
    }
}

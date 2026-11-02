namespace BleUnlockManager.Core.Config;

/// <summary>
/// 配置还缺什么（镜像服务端 <c>BleUnlockService.Config.ConfigCompleteness</c>）。
/// 服务以「未配置 / 缺凭据」状态照常运行；管理端据此决定是否打开设置向导、显示提示。
/// </summary>
public enum SetupState
{
    Complete,
    /// <summary>没有设备：服务不监测任何设备。</summary>
    NoDevice,
    /// <summary>有设备但没有用户名或密码：只自动锁屏，不自动解锁。</summary>
    NoCredentials,
}

public static class SetupCheck
{
    /// <summary>管理通道快照 <c>setup</c> 字段的取值（服务端 ConfigCompleteness.ToWire）。</summary>
    public const string WireNoDevice = "noDevice";
    public const string WireNoCredentials = "noCredentials";

    /// <summary>按磁盘配置判定（密文先解密再判断；解不开的密文按「有值」处理，由校验横幅另行提示）。</summary>
    public static SetupState Evaluate(BleUnlockConfig config)
    {
        var irk = ConfigValidator.NormalizeIrk(ConfigSecrets.DecryptForCompare(config.Device.Irk));
        var undecryptableIrk = ConfigSecrets.IsUnreadableOnThisMachine(config.Device.Irk);
        var hasDevice = undecryptableIrk ||
                        (irk is not null && irk != ConfigValidator.NoDeviceIrk) ||
                        ConfigValidator.NormalizeAddress(config.Device.IdentityAddress) is not null;
        if (!hasDevice)
        {
            return SetupState.NoDevice;
        }

        var hasCredentials = !string.IsNullOrWhiteSpace(config.Credentials.UserName) &&
                             !string.IsNullOrEmpty(config.Credentials.Password);
        return hasCredentials ? SetupState.Complete : SetupState.NoCredentials;
    }

    /// <summary>解析快照里的 setup 字段；未知值按完整处理（不打扰用户）。</summary>
    public static SetupState FromWire(string? code) => code switch
    {
        WireNoDevice => SetupState.NoDevice,
        WireNoCredentials => SetupState.NoCredentials,
        _ => SetupState.Complete,
    };
}

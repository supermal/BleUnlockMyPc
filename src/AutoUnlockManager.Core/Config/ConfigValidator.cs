using System.Globalization;
using AutoUnlockService.Config;
using AutoUnlockManager.Core.Resources;

namespace AutoUnlockManager.Core.Config;

/// <summary>单条校验错误（<paramref name="Field"/> 为点号字段标识，<paramref name="Message"/> 与服务端文案逐字一致）。</summary>
public sealed record ConfigError(string Field, string Message);

/// <summary>校验结果。</summary>
public sealed record ConfigValidationResult(bool IsValid, IReadOnlyList<ConfigError> Errors)
{
    public static ConfigValidationResult Ok() => new(true, []);
    public static ConfigValidationResult Invalid(IReadOnlyList<ConfigError> errors) => new(false, errors);
}

/// <summary>
/// 配置校验（镜像服务端 <c>AutoUnlockService.Config.OptionsValidator</c>）：
/// 规则与提示文案逐字对齐——启动时配置非法会导致服务拒绝启动，保存前必须给出与服务端一致的结论。
/// 上限常量镜像 <c>HelperProtocol</c>（服务端辅助进程拒绝超限值）。
/// </summary>
public static class ConfigValidator
{
    /// <summary>
    /// 未选择设备时的占位 IRK（32 位全零）：保证配置文件仍能通过服务校验，服务照常启动/读取配对库，
    /// 但没有任何真实设备会匹配它，自动解锁实际处于暂停状态。管理端不会把它显示为一个设备。
    /// </summary>
    public const string NoDeviceIrk = "00000000000000000000000000000000";

    /// <summary>镜像 HelperProtocol.MaxSnoozeMinutes。</summary>
    public const double MaxSnoozeMinutes = 240;

    /// <summary>
    /// 归一化 IRK：去空格/冒号/连字符与 0x 前缀，要求恰好 32 位十六进制；无效返回 null。
    /// （镜像服务端 NormalizeIrk，含返回小写。）
    /// </summary>
    public static string? NormalizeIrk(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var cleaned = raw.Trim().Replace(":", "").Replace(" ", "").Replace("-", "");
        if (cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[2..];
        }

        if (cleaned.Length != 32)
        {
            return null;
        }

        foreach (var c in cleaned)
        {
            if (!Uri.IsHexDigit(c))
            {
                return null;
            }
        }

        return cleaned.ToLowerInvariant();
    }

    /// <summary>
    /// 归一化设备地址：去分隔符后恰好 12 位十六进制；空/无效返回 null（空 = 未配置）。
    /// （镜像服务端 NormalizeAddress。）
    /// </summary>
    public static ulong? NormalizeAddress(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var cleaned = raw.Trim().Replace(":", "").Replace("-", "").Replace(" ", "");
        if (cleaned.Length != 12)
        {
            return null;
        }

        return ulong.TryParse(cleaned, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
    }

    /// <summary>归一化地址的显示形式（12 位小写十六进制；未配置返回 null）。</summary>
    public static string? FormatAddress(ulong? normalized)
        => normalized?.ToString("x12", CultureInfo.InvariantCulture);

    /// <summary>校验（消息与顺序镜像服务端 Validate）。</summary>
    public static ConfigValidationResult Validate(AutoUnlockConfig config)
    {
        var errors = new List<ConfigError>();

        // 已加密值（dpapi:v1:）在管理端不显示也不校验其明文：服务端解密后再校验
        // （服务端 Validate 永远运行在明文上，故此处是管理端特有的跳过分支，不参与镜像）
        if (NormalizeIrk(config.Device.Irk) is null && !SecretProtector.IsProtected(config.Device.Irk))
        {
            errors.Add(new ConfigError(ConfigFields.DeviceIrk, string.Format(CoreStrings.Validation_IrkFormat, ConfigFields.DeviceIrk)));
        }

        if (!string.IsNullOrWhiteSpace(config.Device.IdentityAddress) &&
            NormalizeAddress(config.Device.IdentityAddress) is null)
        {
            errors.Add(new ConfigError(ConfigFields.DeviceIdentityAddress,
                string.Format(CoreStrings.Validation_AddressFormat, ConfigFields.DeviceIdentityAddress)));
        }

        if (string.IsNullOrWhiteSpace(config.Credentials.UserName))
        {
            errors.Add(new ConfigError(ConfigFields.CredentialsUserName,
                string.Format(CoreStrings.Validation_UserNameEmpty, ConfigFields.CredentialsUserName)));
        }

        // 控制字符（换行/回车/制表符等）会破坏到 CP 的按行分帧协议；
        // 消息只点名字段、绝不包含字段值
        if ((config.Credentials.UserName ?? "").Any(char.IsControl))
        {
            errors.Add(new ConfigError(ConfigFields.CredentialsUserName,
                string.Format(CoreStrings.Validation_UserNameControlChars, ConfigFields.CredentialsUserName)));
        }
        if ((config.Credentials.Password ?? "").Any(char.IsControl))
        {
            errors.Add(new ConfigError(ConfigFields.CredentialsPassword,
                string.Format(CoreStrings.Validation_PasswordControlChars, ConfigFields.CredentialsPassword)));
        }

        var p = config.Proximity;
        if (p.UnlockThresholdDbm <= p.LockThresholdDbm)
        {
            errors.Add(new ConfigError(ConfigFields.ProximityUnlockThresholdDbm,
                string.Format(CoreStrings.Validation_ThresholdOrder, ConfigFields.ProximityUnlockThresholdDbm)));
        }

        // 批次个数（触发读数不计入，全数达标判定）：下限 1，上限防止收集阶段拖得过长
        if (p.UnlockSampleCount is < 1 or > 20)
        {
            errors.Add(new ConfigError(ConfigFields.ProximityUnlockSampleCount,
                string.Format(CoreStrings.Validation_UnlockCountRange, ConfigFields.ProximityUnlockSampleCount)));
        }
        if (p.LockPromptSampleCount is < 1 or > 50)
        {
            errors.Add(new ConfigError(ConfigFields.ProximityLockPromptSampleCount,
                string.Format(CoreStrings.Validation_LockPromptCountRange, ConfigFields.ProximityLockPromptSampleCount)));
        }
        if (p.LockConfirmSampleCount is < 1 or > 50)
        {
            errors.Add(new ConfigError(ConfigFields.ProximityLockConfirmSampleCount,
                string.Format(CoreStrings.Validation_LockConfirmCountRange, ConfigFields.ProximityLockConfirmSampleCount)));
        }

        if (p.SampleIntervalMs is < 0 or > 2000)
        {
            errors.Add(new ConfigError(ConfigFields.ProximitySampleIntervalMs,
                CoreStrings.Validation_SampleIntervalRange));
        }

        if (p.StartupGraceSeconds < 0)
        {
            errors.Add(new ConfigError(ConfigFields.ProximityStartupGraceSeconds,
                string.Format(CoreStrings.Validation_GraceNegative, ConfigFields.ProximityStartupGraceSeconds)));
        }
        if (p.UnlockPendingTimeoutSeconds <= 0)
        {
            errors.Add(new ConfigError(ConfigFields.ProximityUnlockPendingTimeoutSeconds,
                string.Format(CoreStrings.Validation_PendingTimeoutPositive, ConfigFields.ProximityUnlockPendingTimeoutSeconds)));
        }

        var n = config.Notification;
        // 上限与服务端 HelperProtocol 一致：辅助进程会拒绝超限值，静默拒绝会导致锁屏而无提示
        if (n.RejectSnoozeSeconds <= 0)
        {
            errors.Add(new ConfigError(ConfigFields.NotificationRejectSnoozeSeconds,
                string.Format(CoreStrings.Validation_RejectSnoozePositive, ConfigFields.NotificationRejectSnoozeSeconds)));
        }

        if (!(n.SnoozeMinutes > 0))
        {
            errors.Add(new ConfigError(ConfigFields.NotificationSnoozeMinutes,
                string.Format(CoreStrings.Validation_SnoozePositive, ConfigFields.NotificationSnoozeMinutes)));
        }
        else if (n.SnoozeMinutes > MaxSnoozeMinutes)
        {
            errors.Add(new ConfigError(ConfigFields.NotificationSnoozeMinutes,
                string.Format(CultureInfo.InvariantCulture,
                    CoreStrings.Validation_SnoozeMax, ConfigFields.NotificationSnoozeMinutes, MaxSnoozeMinutes)));
        }

        return errors.Count == 0 ? ConfigValidationResult.Ok() : ConfigValidationResult.Invalid(errors);
    }
}

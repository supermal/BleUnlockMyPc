using System.Globalization;

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

        if (NormalizeIrk(config.Device.Irk) is null)
        {
            errors.Add(new ConfigError(ConfigFields.DeviceIrk, $"{ConfigFields.DeviceIrk} 必须是 32 个十六进制字符"));
        }

        if (!string.IsNullOrWhiteSpace(config.Device.IdentityAddress) &&
            NormalizeAddress(config.Device.IdentityAddress) is null)
        {
            errors.Add(new ConfigError(ConfigFields.DeviceIdentityAddress,
                $"{ConfigFields.DeviceIdentityAddress} 必须是合法的 6 字节 MAC 地址"));
        }

        if (string.IsNullOrWhiteSpace(config.Credentials.UserName))
        {
            errors.Add(new ConfigError(ConfigFields.CredentialsUserName,
                $"{ConfigFields.CredentialsUserName} 不能为空"));
        }

        // 控制字符（换行/回车/制表符等）会破坏到 CP 的按行分帧协议；
        // 消息只点名字段、绝不包含字段值
        if ((config.Credentials.UserName ?? "").Any(char.IsControl))
        {
            errors.Add(new ConfigError(ConfigFields.CredentialsUserName,
                $"{ConfigFields.CredentialsUserName} 不能包含控制字符（换行、回车、制表符等）"));
        }
        if ((config.Credentials.Password ?? "").Any(char.IsControl))
        {
            errors.Add(new ConfigError(ConfigFields.CredentialsPassword,
                $"{ConfigFields.CredentialsPassword} 不能包含控制字符（换行、回车、制表符等）"));
        }

        var p = config.Proximity;
        if (p.UnlockThresholdDbm <= p.LockThresholdDbm)
        {
            errors.Add(new ConfigError(ConfigFields.ProximityUnlockThresholdDbm,
                $"{ConfigFields.ProximityUnlockThresholdDbm} 必须大于 LockThresholdDbm，否则死区为负"));
        }

        // 批次个数（触发读数不计入，全数达标判定）：下限 1，上限防止收集阶段拖得过长
        if (p.UnlockSampleCount is < 1 or > 20)
        {
            errors.Add(new ConfigError(ConfigFields.ProximityUnlockSampleCount,
                $"{ConfigFields.ProximityUnlockSampleCount} 必须在 1 到 20 之间"));
        }
        if (p.LockPromptSampleCount is < 1 or > 50)
        {
            errors.Add(new ConfigError(ConfigFields.ProximityLockPromptSampleCount,
                $"{ConfigFields.ProximityLockPromptSampleCount} 必须在 1 到 50 之间"));
        }
        if (p.LockConfirmSampleCount is < 1 or > 50)
        {
            errors.Add(new ConfigError(ConfigFields.ProximityLockConfirmSampleCount,
                $"{ConfigFields.ProximityLockConfirmSampleCount} 必须在 1 到 50 之间"));
        }

        if (p.StartupGraceSeconds < 0)
        {
            errors.Add(new ConfigError(ConfigFields.ProximityStartupGraceSeconds,
                $"{ConfigFields.ProximityStartupGraceSeconds} 不能为负数"));
        }
        if (p.UnlockPendingTimeoutSeconds <= 0)
        {
            errors.Add(new ConfigError(ConfigFields.ProximityUnlockPendingTimeoutSeconds,
                $"{ConfigFields.ProximityUnlockPendingTimeoutSeconds} 必须为正数"));
        }

        var n = config.Notification;
        // 上限与服务端 HelperProtocol 一致：辅助进程会拒绝超限值，静默拒绝会导致锁屏而无提示
        if (n.RejectSnoozeSeconds <= 0)
        {
            errors.Add(new ConfigError(ConfigFields.NotificationRejectSnoozeSeconds,
                $"{ConfigFields.NotificationRejectSnoozeSeconds} 必须为正数"));
        }

        if (!(n.SnoozeMinutes > 0))
        {
            errors.Add(new ConfigError(ConfigFields.NotificationSnoozeMinutes,
                $"{ConfigFields.NotificationSnoozeMinutes} 必须为正数"));
        }
        else if (n.SnoozeMinutes > MaxSnoozeMinutes)
        {
            errors.Add(new ConfigError(ConfigFields.NotificationSnoozeMinutes,
                string.Create(CultureInfo.InvariantCulture,
                    $"{ConfigFields.NotificationSnoozeMinutes} 不能超过 {MaxSnoozeMinutes:0} 分钟")));
        }

        return errors.Count == 0 ? ConfigValidationResult.Ok() : ConfigValidationResult.Invalid(errors);
    }
}

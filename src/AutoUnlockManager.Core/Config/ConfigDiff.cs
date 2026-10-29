using AutoUnlockService.Config;

namespace AutoUnlockManager.Core.Config;

/// <summary>
/// 单条配置差异（保存前确认对话框展示）。
/// <paramref name="IsSensitive"/> 为真时（密码）不携带新旧值，对话框显示「已修改」。
/// </summary>
public sealed record ConfigChange(
    string Field,
    string DisplayName,
    string? OldText,
    string? NewText,
    bool IsSensitive);

/// <summary>
/// 配置差异计算：比较归一化后的值（Irk 重排版不算变更）；敏感字段按解密后的明文比较
/// （磁盘密文与表单回显的明文同值不算变更）；输出顺序与表单字段一致。
/// 「无差异」判定同时用于「没有需要保存的更改」提示与编辑脏标记。
/// </summary>
public static class ConfigDiff
{
    /// <summary>计算从 <paramref name="from"/>（最近一次加载/写盘快照）到 <paramref name="to"/> 的差异列表。</summary>
    public static IReadOnlyList<ConfigChange> Compute(AutoUnlockConfig from, AutoUnlockConfig to)
    {
        var changes = new List<ConfigChange>();

        var oldIrkPlain = ConfigSecrets.DecryptForCompare(from.Device.Irk);
        var newIrkPlain = ConfigSecrets.DecryptForCompare(to.Device.Irk);
        var oldIrk = ConfigValidator.NormalizeIrk(oldIrkPlain) ?? oldIrkPlain;
        var newIrk = ConfigValidator.NormalizeIrk(newIrkPlain) ?? newIrkPlain;
        if (!string.Equals(oldIrk, newIrk, StringComparison.Ordinal))
        {
            changes.Add(new ConfigChange(ConfigFields.DeviceIrk, GetDisplayName(ConfigFields.DeviceIrk),
                FormatKeyOrProtected(oldIrk), FormatKeyOrProtected(newIrk), IsSensitive: false));
        }

        if (from.Device.IrkByteOrder != to.Device.IrkByteOrder)
        {
            changes.Add(new ConfigChange(ConfigFields.DeviceIrkByteOrder, GetDisplayName(ConfigFields.DeviceIrkByteOrder),
                FormatIrkByteOrder(from.Device.IrkByteOrder), FormatIrkByteOrder(to.Device.IrkByteOrder), IsSensitive: false));
        }

        var oldAddress = ConfigValidator.NormalizeAddress(from.Device.IdentityAddress);
        var newAddress = ConfigValidator.NormalizeAddress(to.Device.IdentityAddress);
        if (oldAddress != newAddress)
        {
            changes.Add(new ConfigChange(ConfigFields.DeviceIdentityAddress, GetDisplayName(ConfigFields.DeviceIdentityAddress),
                FormatAddress(oldAddress), FormatAddress(newAddress), IsSensitive: false));
        }

        if (!string.Equals(from.Credentials.UserName, to.Credentials.UserName, StringComparison.Ordinal))
        {
            changes.Add(new ConfigChange(ConfigFields.CredentialsUserName, GetDisplayName(ConfigFields.CredentialsUserName),
                FormatText(from.Credentials.UserName), FormatText(to.Credentials.UserName), IsSensitive: false));
        }

        if (!string.Equals(ConfigSecrets.DecryptForCompare(from.Credentials.Password),
                ConfigSecrets.DecryptForCompare(to.Credentials.Password), StringComparison.Ordinal))
        {
            // 密码不进入对话框文本
            changes.Add(new ConfigChange(ConfigFields.CredentialsPassword, GetDisplayName(ConfigFields.CredentialsPassword),
                OldText: null, NewText: null, IsSensitive: true));
        }

        AddNumber(changes, ConfigFields.ProximityLockThresholdDbm,
            from.Proximity.LockThresholdDbm, to.Proximity.LockThresholdDbm);
        AddNumber(changes, ConfigFields.ProximityUnlockThresholdDbm,
            from.Proximity.UnlockThresholdDbm, to.Proximity.UnlockThresholdDbm);

        AddCount(changes, ConfigFields.ProximityUnlockSampleCount,
            from.Proximity.UnlockSampleCount, to.Proximity.UnlockSampleCount);
        AddCount(changes, ConfigFields.ProximityLockPromptSampleCount,
            from.Proximity.LockPromptSampleCount, to.Proximity.LockPromptSampleCount);
        AddCount(changes, ConfigFields.ProximityLockConfirmSampleCount,
            from.Proximity.LockConfirmSampleCount, to.Proximity.LockConfirmSampleCount);
        AddCount(changes, ConfigFields.ProximitySampleIntervalMs,
            from.Proximity.SampleIntervalMs, to.Proximity.SampleIntervalMs);

        AddNumber(changes, ConfigFields.ProximityStartupGraceSeconds,
            from.Proximity.StartupGraceSeconds, to.Proximity.StartupGraceSeconds);
        AddNumber(changes, ConfigFields.ProximityUnlockPendingTimeoutSeconds,
            from.Proximity.UnlockPendingTimeoutSeconds, to.Proximity.UnlockPendingTimeoutSeconds);

        if (from.Notification.Mode != to.Notification.Mode)
        {
            changes.Add(new ConfigChange(ConfigFields.NotificationMode, GetDisplayName(ConfigFields.NotificationMode),
                FormatNotifierMode(from.Notification.Mode), FormatNotifierMode(to.Notification.Mode), IsSensitive: false));
        }

        AddNumber(changes, ConfigFields.NotificationRejectSnoozeSeconds,
            from.Notification.RejectSnoozeSeconds, to.Notification.RejectSnoozeSeconds);
        AddNumber(changes, ConfigFields.NotificationSnoozeMinutes,
            from.Notification.SnoozeMinutes, to.Notification.SnoozeMinutes);

        return changes;
    }

    /// <summary>字段中文名（表单标签/差异对话框共用）。未知字段回退点号标识。</summary>
    /// <summary>
    /// 未保存更改栏的一行摘要：单项「锁屏确认个数 10 → 12」，多项「锁屏确认个数等 3 项」。
    /// 密码与 IRK 不显示取值（界面不展示 IRK），只写「已修改」。
    /// </summary>
    public static string Summarize(IReadOnlyList<ConfigChange> changes)
    {
        if (changes.Count == 0)
        {
            return "";
        }

        var first = changes[0];
        if (changes.Count > 1)
        {
            return $"{first.DisplayName}等 {changes.Count} 项";
        }

        return first.IsSensitive || first.Field == ConfigFields.DeviceIrk
            ? $"{first.DisplayName} 已修改"
            : $"{first.DisplayName} {first.OldText} → {first.NewText}";
    }

    public static string GetDisplayName(string field) => field switch
    {
        ConfigFields.DeviceIrk => "设备 IRK",
        ConfigFields.DeviceIrkByteOrder => "IRK 字节序",
        ConfigFields.DeviceIdentityAddress => "设备地址",
        ConfigFields.CredentialsUserName => "凭据用户名",
        ConfigFields.CredentialsPassword => "凭据密码",
        ConfigFields.ProximityLockThresholdDbm => "锁定阈值（dBm）",
        ConfigFields.ProximityUnlockThresholdDbm => "解锁阈值（dBm）",
        ConfigFields.ProximityUnlockSampleCount => "解锁确认个数",
        ConfigFields.ProximityLockPromptSampleCount => "弹窗触发个数",
        ConfigFields.ProximityLockConfirmSampleCount => "锁屏确认个数",
        ConfigFields.ProximitySampleIntervalMs => "RSSI 采样间隔（毫秒）",
        ConfigFields.ProximityStartupGraceSeconds => "启动宽限（秒）",
        ConfigFields.ProximityUnlockPendingTimeoutSeconds => "解锁等待超时（秒）",
        ConfigFields.NotificationMode => "通知方式",
        ConfigFields.NotificationRejectSnoozeSeconds => "拒绝后暂缓（秒）",
        ConfigFields.NotificationSnoozeMinutes => "稍后提醒（分钟）",
        _ => field,
    };

    /// <summary>IRK 字节序中文标签（表单下拉与差异共用）。</summary>
    public static string FormatIrkByteOrder(IrkByteOrder value) => value switch
    {
        IrkByteOrder.Auto => "自动检测（Auto）",
        IrkByteOrder.Normal => "正序（Normal）",
        IrkByteOrder.Reversed => "反序（Reversed）",
        _ => value.ToString(),
    };

    /// <summary>通知方式中文标签。</summary>
    public static string FormatNotifierMode(NotifierMode value) => value switch
    {
        NotifierMode.Toast => "系统通知（Toast）",
        NotifierMode.Dialog => "对话框（Dialog）",
        _ => value.ToString(),
    };

    private static void AddNumber(List<ConfigChange> changes, string field, double from, double to)
    {
        if (from != to) // NaN 参与时恒为变更（脏标记哨兵）
        {
            changes.Add(new ConfigChange(field, GetDisplayName(field),
                ConfigInput.FormatNumber(from), ConfigInput.FormatNumber(to), IsSensitive: false));
        }
    }

    private static void AddCount(List<ConfigChange> changes, string field, int from, int to)
    {
        if (from != to)
        {
            changes.Add(new ConfigChange(field, GetDisplayName(field),
                ConfigInput.FormatCount(from), ConfigInput.FormatCount(to), IsSensitive: false));
        }
    }

    /// <summary>差异比较已完成解密：仅无法解密的密文兜底显示「（已加密）」，其余按敏感键截断。</summary>
    private static string FormatKeyOrProtected(string value)
        => SecretProtector.IsProtected(value) ? "（已加密）" : FormatKey(value);

    /// <summary>敏感键截断显示：前 8…后 4（过短则原样）。</summary>
    private static string FormatKey(string? value)
    {
        value ??= ""; // 防御：加载/表单路径已收敛 null（评审 I1），此处兜底不抛
        if (value.Length == 0)
        {
            return "（未设置）";
        }

        return value.Length <= 12 ? value : $"{value[..8]}…{value[^4..]}";
    }

    private static string FormatAddress(ulong? normalized)
        => ConfigValidator.FormatAddress(normalized) ?? "（未设置）";

    private static string FormatText(string? value) => string.IsNullOrEmpty(value) ? "（空）" : value;
}

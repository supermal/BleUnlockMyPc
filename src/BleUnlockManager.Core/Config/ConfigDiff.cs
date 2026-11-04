using BleUnlockService.Config;
using BleUnlockManager.Core.Resources;

namespace BleUnlockManager.Core.Config;

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
    public static IReadOnlyList<ConfigChange> Compute(BleUnlockConfig from, BleUnlockConfig to)
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
        AddNumber(changes, ConfigFields.ProximityNoSignalDepartureSeconds,
            from.Proximity.NoSignalDepartureSeconds, to.Proximity.NoSignalDepartureSeconds);

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
            return string.Format(CoreStrings.Diff_FirstAndMore, first.DisplayName, changes.Count);
        }

        return first.IsSensitive || first.Field == ConfigFields.DeviceIrk
            ? string.Format(CoreStrings.Diff_FieldModified, first.DisplayName)
            : $"{first.DisplayName} {first.OldText} → {first.NewText}";
    }

    public static string GetDisplayName(string field) => field switch
    {
        ConfigFields.DeviceIrk => CoreStrings.Field_DeviceIrk,
        ConfigFields.DeviceIrkByteOrder => CoreStrings.Field_IrkByteOrder,
        ConfigFields.DeviceIdentityAddress => CoreStrings.Field_DeviceAddress,
        ConfigFields.CredentialsUserName => CoreStrings.Field_UserName,
        ConfigFields.CredentialsPassword => CoreStrings.Field_Password,
        ConfigFields.ProximityLockThresholdDbm => CoreStrings.Field_LockThreshold,
        ConfigFields.ProximityUnlockThresholdDbm => CoreStrings.Field_UnlockThreshold,
        ConfigFields.ProximityUnlockSampleCount => CoreStrings.Field_UnlockSampleCount,
        ConfigFields.ProximityLockPromptSampleCount => CoreStrings.Field_LockPromptSampleCount,
        ConfigFields.ProximityLockConfirmSampleCount => CoreStrings.Field_LockConfirmSampleCount,
        ConfigFields.ProximitySampleIntervalMs => CoreStrings.Field_SampleInterval,
        ConfigFields.ProximityStartupGraceSeconds => CoreStrings.Field_StartupGrace,
        ConfigFields.ProximityUnlockPendingTimeoutSeconds => CoreStrings.Field_UnlockPendingTimeout,
        ConfigFields.ProximityNoSignalDepartureSeconds => CoreStrings.Field_NoSignalDeparture,
        ConfigFields.NotificationMode => CoreStrings.Field_NotificationMode,
        ConfigFields.NotificationRejectSnoozeSeconds => CoreStrings.Field_RejectSnooze,
        ConfigFields.NotificationSnoozeMinutes => CoreStrings.Field_Snooze,
        _ => field,
    };

    /// <summary>IRK 字节序中文标签（表单下拉与差异共用）。</summary>
    public static string FormatIrkByteOrder(IrkByteOrder value) => value switch
    {
        IrkByteOrder.Auto => CoreStrings.ByteOrder_Auto,
        IrkByteOrder.Normal => CoreStrings.ByteOrder_Normal,
        IrkByteOrder.Reversed => CoreStrings.ByteOrder_Reversed,
        _ => value.ToString(),
    };

    /// <summary>通知方式中文标签。</summary>
    public static string FormatNotifierMode(NotifierMode value) => value switch
    {
        NotifierMode.Toast => CoreStrings.Mode_Toast,
        NotifierMode.Dialog => CoreStrings.Mode_Dialog,
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
        => SecretProtector.IsProtected(value) ? CoreStrings.Value_Encrypted : FormatKey(value);

    /// <summary>敏感键截断显示：前 8…后 4（过短则原样）。</summary>
    private static string FormatKey(string? value)
    {
        value ??= ""; // 防御：加载/表单路径已收敛 null（评审 I1），此处兜底不抛
        if (value.Length == 0)
        {
            return CoreStrings.Value_NotSet;
        }

        return value.Length <= 12 ? value : $"{value[..8]}…{value[^4..]}";
    }

    private static string FormatAddress(ulong? normalized)
        => ConfigValidator.FormatAddress(normalized) ?? CoreStrings.Value_NotSet;

    private static string FormatText(string? value) => string.IsNullOrEmpty(value) ? CoreStrings.Value_Empty : value;
}

using AutoUnlockService.Config;
using AutoUnlockManager.Core.Resources;

namespace AutoUnlockManager.Core.Config;

/// <summary>
/// 敏感字段（<c>Device.Irk</c> / <c>Credentials.Password</c>）在管理端的处理约定：
/// 配置页显示当前值——磁盘密文在本机解密后填入表单（解不开则为空并要求重新输入）；
/// 修改后保存时把新输入的明文替换为密文（空值与已是密文的值跳过）。
/// 差异比较按解密后的明文进行（同一值「密文 ↔ 明文」不算变更）。
/// 加密实现与服务端共享同一文件（SecretProtector），格式不会漂移。
/// </summary>
public static class ConfigSecrets
{
    /// <summary>表单展示文本：密文解密为明文显示（解不开返回空串，配合「必须重新输入」提示）；明文原样。</summary>
    public static string DisplayText(string loadedValue)
    {
        if (!SecretProtector.IsProtected(loadedValue))
        {
            return loadedValue;
        }

        return SecretProtector.TryUnprotect(loadedValue, out var plain) ? plain : "";
    }

    /// <summary>密文且无法在本机解密（跨机器拷贝/损坏）→ 必须重新输入；明文与可解密密文返回 false。</summary>
    public static bool IsUnreadableOnThisMachine(string loadedValue)
        => SecretProtector.IsProtected(loadedValue) && !SecretProtector.TryUnprotect(loadedValue, out _);

    /// <summary>差异比较用明文：密文解密（解不开原样返回——与任何明文都不相等，视为变更）；明文/空值原样。</summary>
    public static string DecryptForCompare(string? value)
    {
        value ??= "";
        return SecretProtector.IsProtected(value) && SecretProtector.TryUnprotect(value, out var plain)
            ? plain
            : value;
    }

    /// <summary>保存前把新输入的明文加密（空值与已是密文的值跳过）；返回是否发生了加密。</summary>
    public static bool ProtectNewValues(AutoUnlockConfig config)
    {
        var changed = false;
        config.Device.Irk = ProtectIfNeeded(config.Device.Irk, ref changed);
        config.Credentials.Password = ProtectIfNeeded(config.Credentials.Password, ref changed);
        return changed;
    }

    /// <summary>字段下加密提示文案（空串 = 无提示）。</summary>
    public static string HintText(string loadedValue)
    {
        if (SecretProtector.IsProtected(loadedValue))
        {
            return SecretProtector.TryUnprotect(loadedValue, out _)
                ? CoreStrings.Secret_StoredEncrypted
                : CoreStrings.Secret_Undecryptable;
        }

        return loadedValue.Length > 0 ? CoreStrings.Secret_Plaintext : "";
    }

    private static string ProtectIfNeeded(string value, ref bool changed)
    {
        if (value.Length == 0 || SecretProtector.IsProtected(value))
        {
            return value;
        }

        changed = true;
        return SecretProtector.Protect(value);
    }
}

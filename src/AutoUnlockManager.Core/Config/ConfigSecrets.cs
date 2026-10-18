using AutoUnlockService.Config;

namespace AutoUnlockManager.Core.Config;

/// <summary>
/// 敏感字段（<c>Device.Irk</c> / <c>Credentials.Password</c>）在管理端的处理约定：
/// 已加密值（<c>dpapi:v1:</c>）不回显、不校验其明文，表单「留空保持不变」；
/// 保存时把新输入的明文替换为密文（旧密文与空值跳过）。
/// 加密实现与服务端共享同一文件（SecretProtector），格式不会漂移。
/// </summary>
public static class ConfigSecrets
{
    /// <summary>表单展示文本：密文不显示（返回空串，配合提示「留空保持不变」），明文原样。</summary>
    public static string DisplayText(string loadedValue)
        => SecretProtector.IsProtected(loadedValue) ? "" : loadedValue;

    /// <summary>密文且无法在本机解密（跨机器拷贝/损坏）→ 必须重新输入；明文与可解密密文返回 false。</summary>
    public static bool IsUnreadableOnThisMachine(string loadedValue)
        => SecretProtector.IsProtected(loadedValue) && !SecretProtector.TryUnprotect(loadedValue, out _);

    /// <summary>表单文本 → 写入值：留空 + 已加载密文 = 保留原密文；否则用表单文本。</summary>
    public static string ResolveInput(string? text, string loadedValue)
        => string.IsNullOrEmpty(text) && SecretProtector.IsProtected(loadedValue) ? loadedValue : text ?? "";

    /// <summary>保存前把新输入的明文加密（空值与已是密文的值跳过）；返回是否发生了加密。</summary>
    public static bool ProtectNewValues(AutoUnlockConfig config)
    {
        var changed = false;
        config.Device.Irk = ProtectIfNeeded(config.Device.Irk, ref changed);
        config.Credentials.Password = ProtectIfNeeded(config.Credentials.Password, ref changed);
        return changed;
    }

    /// <summary>字段下加密提示文案（空串 = 无提示；密文存储时展示）。</summary>
    public static string HintText(string loadedValue)
    {
        if (!SecretProtector.IsProtected(loadedValue))
        {
            return "";
        }

        return SecretProtector.TryUnprotect(loadedValue, out _)
            ? "已加密存储；留空保持不变，输入新值将替换。"
            : "该加密值无法在本机解密（可能来自其他机器）；必须重新输入。";
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

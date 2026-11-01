using BleUnlockService.Resources;

namespace BleUnlockService.Config;

/// <summary>
/// 配置中敏感值的解析（解密）：把 <c>Device.Irk</c> / <c>Credentials.Password</c> 上的
/// <c>dpapi:v1:</c> 密文就地替换为明文后再进入校验与运行；明文视为兼容输入并给出警告
/// （管理端保存时自动加密，见 SecretProtector）。密文无法解密（跨机器拷贝/损坏）视为错误，
/// 走「配置校验失败，已保留原配置」同一路径（热重载）或拒绝启动（服务启动时）。
/// </summary>
public static class OptionsSecrets
{
    /// <summary>
    /// 就地解析两个敏感字段；返回 <c>false</c> 表示存在无法解密的密文（<paramref name="errors"/> 非空）。
    /// <paramref name="warnings"/>：明文存储提示（不阻断）。
    /// </summary>
    public static bool TryResolve(BleUnlockOptions options,
        out IReadOnlyList<string> warnings, out IReadOnlyList<string> errors)
    {
        var warningList = new List<string>();
        var errorList = new List<string>();

        Resolve("Device.Irk", options.Device.Irk, v => options.Device.Irk = v, warningList, errorList);
        Resolve("Credentials.Password", options.Credentials.Password, v => options.Credentials.Password = v,
            warningList, errorList);

        warnings = warningList;
        errors = errorList;
        return errorList.Count == 0;
    }

    private static void Resolve(string field, string value, Action<string> set,
        List<string> warnings, List<string> errors)
    {
        if (value.Length == 0)
        {
            return; // 未设置：不加密也不提示
        }

        if (!SecretProtector.IsProtected(value))
        {
            warnings.Add(string.Format(ServiceStrings.Validation_PlaintextSecret, field));
            return;
        }

        if (SecretProtector.TryUnprotect(value, out var plaintext))
        {
            set(plaintext); // 就地替换为明文：后续校验与运行只见明文
        }
        else
        {
            errors.Add(string.Format(ServiceStrings.Validation_SecretUndecryptable, field));
        }
    }
}

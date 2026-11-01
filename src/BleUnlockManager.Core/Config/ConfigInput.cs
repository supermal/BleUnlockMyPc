using System.Globalization;

namespace BleUnlockManager.Core.Config;

/// <summary>
/// 表单文本与配置值的互转。
/// 输入解析容忍「.」或「,」作小数点（es-ES 键盘习惯；两者同现视为无效），拒绝 NaN/∞；
/// 展示一律 InvariantCulture（避免系统区域影响）。
/// </summary>
public static class ConfigInput
{
    /// <summary>解析实数（接受 `,` 作小数点；拒绝 NaN/∞/空/非法）。</summary>
    public static bool TryParseNumber(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.Trim().Replace(" ", "");
        var hasDot = normalized.Contains('.');
        var hasComma = normalized.Contains(',');

        if (hasDot && hasComma)
        {
            return false; // 「1.234,5」类混合写法歧义，视为无效
        }

        if (hasComma)
        {
            normalized = normalized.Replace(',', '.');
        }

        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        if (!double.IsFinite(value))
        {
            value = 0; // NaN/∞：复位传出值，调用方拿到干净结果
            return false;
        }

        return true;
    }

    /// <summary>解析整数（批次个数等；“3.5”视为无效）。</summary>
    public static bool TryParseCount(string? text, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>实数 → 展示/编辑文本（InvariantCulture；最短往返形式）。</summary>
    public static string FormatNumber(double value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>整数 → 展示/编辑文本。</summary>
    public static string FormatCount(int value) => value.ToString(CultureInfo.InvariantCulture);
}

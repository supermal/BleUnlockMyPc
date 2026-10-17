using System.Globalization;
using AutoUnlockManager.Core.Logs;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Windows.UI.Text;

namespace AutoUnlockManager.Helpers;

/// <summary>
/// 日志行模板的静态视觉映射（x:Bind 函数绑定）。刻意不依赖主题：
/// 强调用固定语义色（WRN 琥珀 / ERR 红，低透明度底纹）与透明度/字重；
/// 文本色交给 ThemeResource 自行翻转——切换亮暗主题无需重建列表。
/// </summary>
public static class LogRowVisuals
{
    /// <summary>WRN 行底纹可见性。</summary>
    public static Visibility WarningTint(LogSeverity severity)
        => severity == LogSeverity.Warning ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>ERR/FTL 行底纹可见性。</summary>
    public static Visibility ErrorTint(LogSeverity severity)
        => severity is LogSeverity.Error or LogSeverity.Fatal ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>消息文本透明度（DBG 变暗、未知行微暗、其余正常）。</summary>
    public static double TextOpacity(LogSeverity severity) => severity switch
    {
        LogSeverity.Debug => 0.55,
        LogSeverity.Unknown => 0.75,
        _ => 1.0,
    };

    /// <summary>消息字重（ERR/FTL 半粗）。</summary>
    public static FontWeight MessageWeight(LogSeverity severity)
        => severity is LogSeverity.Error or LogSeverity.Fatal ? FontWeights.SemiBold : FontWeights.Normal;

    /// <summary>文件大小展示（B/KB/MB，InvariantCulture）。</summary>
    public static string FileSizeText(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{(bytes / 1024.0).ToString("F1", CultureInfo.InvariantCulture)} KB",
        _ => $"{(bytes / (1024.0 * 1024)).ToString("F1", CultureInfo.InvariantCulture)} MB",
    };
}

using System.Drawing;

namespace AutoUnlockManager.Services;

/// <summary>
/// 托盘状态色（设计 §5.4）：绿=正常、红=停止/未安装、灰=未知、黄=降级（保留）。
/// 图标在运行时按这些色值自绘，无第三方美术资产。
/// </summary>
internal static class TrayStatusColors
{
    /// <summary>绿：服务运行。</summary>
    public static readonly Color Running = Color.FromArgb(16, 124, 16);

    /// <summary>红：服务已停止或未安装。</summary>
    public static readonly Color StoppedOrNotInstalled = Color.FromArgb(209, 52, 56);

    /// <summary>灰：查询失败/未知。</summary>
    public static readonly Color Unknown = Color.FromArgb(122, 122, 122);

    /// <summary>黄：降级/告警（保留色，Task 4 遥测降级后启用）。</summary>
    public static readonly Color Degraded = Color.FromArgb(242, 169, 0);
}

using Microsoft.UI;
using Microsoft.Windows.Storage.Pickers;

namespace AutoUnlockManager.Helpers;

/// <summary>
/// 保存文件对话框封装（WinAppSDK <c>Microsoft.Windows.Storage.Pickers</c> 新 API）。
/// 选型：旧版 <c>Windows.Storage.Pickers.FileSavePicker</c> 的选择器代理受完整性级别限制，
/// 在提权（requireAdministrator，本应用清单如此）进程中必然失败——烟测「导出失败」即此因；
/// 新 API 专为提权与未打包桌面场景设计（本机 WinAppSDK 2.x foundation 已带投影）。
/// </summary>
public static class SaveFilePicker
{
    /// <summary>
    /// 弹出保存对话框。用户选定后返回目标完整路径；取消返回 <see langword="null"/>。
    /// </summary>
    /// <param name="windowId">宿主窗口 ID（提权/未打包场景必须有宿主窗口）。</param>
    /// <param name="suggestedFileName">建议文件名（可带或不带扩展名）。</param>
    /// <param name="typeLabel">文件类型显示名（如「文本文件」）。</param>
    /// <param name="extensions">允许的扩展名（如 <c>[".txt"]</c>）。</param>
    public static async Task<string?> PickAsync(
        WindowId windowId,
        string suggestedFileName,
        string typeLabel,
        IReadOnlyList<string> extensions)
    {
        var picker = new FileSavePicker(windowId)
        {
            SuggestedFileName = suggestedFileName,
        };

        picker.FileTypeChoices.Add(typeLabel, new List<string>(extensions));

        var result = await picker.PickSaveFileAsync();
        return result?.Path;
    }
}

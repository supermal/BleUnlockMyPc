namespace AutoUnlockManager.Core;

/// <summary>
/// 管理端与服务端共享的固定路径常量（路径语义与设计 §4 一致，无需探测）。
/// 服务端 Program.cs 的 DataDir 与 <see cref="DataDir"/> 为同一表达式。
/// </summary>
public static class AppPaths
{
    /// <summary>服务数据目录：%ProgramData%\AutoUnlock。</summary>
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AutoUnlock");

    /// <summary>服务配置文件：DataDir\appsettings.json（服务热重载监听的文件）。</summary>
    public static string ConfigFile { get; } = Path.Combine(DataDir, "appsettings.json");

    /// <summary>服务日志目录：DataDir\logs。</summary>
    public static string LogsDir { get; } = Path.Combine(DataDir, "logs");

    /// <summary>服务日志文件名模式：autounlock-*.log（Serilog 按日滚动）。</summary>
    public static string LogFilePattern { get; } = "autounlock-*.log";

    /// <summary>管理端自身设置目录：%LocalAppData%\AutoUnlockManager。</summary>
    public static string ManagerAppDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoUnlockManager");

    /// <summary>管理端自身设置文件：ManagerAppDataDir\settings.json（窗口位置等非敏感项）。</summary>
    public static string ManagerSettingsFile { get; } = Path.Combine(ManagerAppDataDir, "settings.json");
}

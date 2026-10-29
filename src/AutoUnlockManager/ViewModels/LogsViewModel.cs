using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using AutoUnlockManager.Core;
using AutoUnlockManager.Core.Logs;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace AutoUnlockManager.ViewModels;

/// <summary>
/// 日志页视图模型（设计 §5.3 日志页）：文件清单/选择、实时跟随（后台轮询 + Dispatcher 投递）、
/// 暂停冻结/继续追上、四级别开关 + 关键字过滤（250ms 防抖）、行数上限裁剪、导出当前过滤视图。
/// 生命周期：页面导航离开时调用 <see cref="Stop"/>（不缓存页面）。
/// </summary>
public sealed partial class LogsViewModel : ObservableObject
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RotateCheckInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan KeywordDebounce = TimeSpan.FromMilliseconds(250);

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _keywordTimer;
    private bool _initialized;
    private readonly object _followGate = new();

    /// <summary>全量行（超过上限后按块从头部裁剪）。</summary>
    private List<LogEntry> _all = [];

    /// <summary>跟随循环的续行级别携带值。</summary>
    private LogSeverity _carry = LogSeverity.Unknown;

    private CancellationTokenSource? _cts;
    private long _position;
    private bool _followNewest;
    private bool _fileMissingNotified;

    public LogsViewModel()
    {
        _keywordTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = KeywordDebounce };
        _keywordTimer.Tick += (_, _) =>
        {
            _keywordTimer.Stop();
            RebuildVisible();
        };

        VisibleEntries = [];
        Keyword = "";
        StatusText = "（未选择文件）";
        LogsDirText = AppPaths.LogsDir;
        OpenFolderPath = AppPaths.DataDir;
        BannerTitle = "";
        BannerMessage = "";
        ShowDebug = ShowInformation = ShowWarning = ShowError = true;
        AutoScroll = true;
    }

    /// <summary>过滤视图整体重建后触发（过滤/加载/裁剪）；增量追加不触发——
    /// 跟随滚动交给列表的 ItemsUpdatingScrollMode（底部才跟随）。</summary>
    public event Action? ViewRebuilt;

    // —— 文件清单 / 状态 ——

    public ObservableCollection<LogFileInfo> Files { get; } = [];

    [ObservableProperty]
    public partial LogFileInfo? SelectedFile { get; set; }

    [ObservableProperty]
    public partial bool LogsDirAvailable { get; set; }

    [ObservableProperty]
    public partial string LogsDirText { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    public partial bool CanFollow { get; set; }

    /// <summary>「打开所在文件夹」目标（日志目录缺失时回退 DataDir）。</summary>
    [ObservableProperty]
    public partial string OpenFolderPath { get; set; }

    // —— 跟随控制 ——

    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    [ObservableProperty]
    public partial bool AutoScroll { get; set; }

    // —— 过滤 ——

    [ObservableProperty]
    public partial bool ShowDebug { get; set; }

    [ObservableProperty]
    public partial bool ShowInformation { get; set; }

    [ObservableProperty]
    public partial bool ShowWarning { get; set; }

    [ObservableProperty]
    public partial bool ShowError { get; set; }

    [ObservableProperty]
    public partial string Keyword { get; set; }

    /// <summary>当前过滤视图（ItemsSource；增量追加、过滤重建或裁剪时整体替换）。</summary>
    [ObservableProperty]
    public partial ObservableCollection<LogEntry> VisibleEntries { get; set; }

    // —— 横幅 ——

    [ObservableProperty]
    public partial bool BannerVisible { get; set; }

    [ObservableProperty]
    public partial InfoBarSeverity BannerSeverity { get; set; }

    [ObservableProperty]
    public partial string BannerTitle { get; set; }

    [ObservableProperty]
    public partial string BannerMessage { get; set; }

    /// <summary>行数上限（超出按 1/10 从头部裁剪）；internal 供 diag 注入小值。</summary>
    internal int MaxLineCount { get; set; } = 200_000;

    /// <summary>暂停/继续按钮文案。</summary>
    public string PauseButtonText => IsPaused ? "继续跟随" : "暂停跟随";

    /// <summary>当前过滤视图行数（导出提示用）。</summary>
    public int VisibleCount => VisibleEntries.Count;

    /// <summary>初始化：扫描文件清单并选中最新文件开始跟随。</summary>
    /// <summary>
    /// Se llama una vez al arrancar la aplicación: el seguimiento continúa aunque la página no esté
    /// visible, porque la navegación lateral muestra el número de advertencias del día.
    /// </summary>
    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        RefreshFiles();
    }

    // —— Recuentos y seguimiento visual ——

    /// <summary>Recuento por nivel del fichero actual (botones de filtro y contador de la navegación).</summary>
    [ObservableProperty] public partial LogLevelCounts Counts { get; set; } = LogLevelCounts.Empty;

    /// <summary>La lista está al final (lo informa la página a partir de su ScrollViewer).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFollowing), nameof(IsBrowsing))]
    public partial bool IsAtBottom { get; set; } = true;

    /// <summary>Líneas nuevas (que pasan el filtro) llegadas mientras se revisa el historial.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingNewLines))]
    public partial int PendingNewLines { get; set; }

    public bool HasPendingNewLines => PendingNewLines > 0;

    public bool IsFollowing => IsAtBottom && AutoScroll;

    public bool IsBrowsing => !IsFollowing;

    [ObservableProperty] public partial string LineCountText { get; set; } = "";

    partial void OnIsAtBottomChanged(bool value)
    {
        if (value)
        {
            PendingNewLines = 0;
        }
    }

    partial void OnAutoScrollChanged(bool value)
    {
        OnPropertyChanged(nameof(IsFollowing));
        OnPropertyChanged(nameof(IsBrowsing));
    }

    /// <summary>刷新文件清单（「刷新」按钮/换日/初始化）；保持当前选择（同路径重载），已消失则回退最新。</summary>
    public void RefreshFiles()
    {
        StopLoop();

        Files.Clear();
        var files = LogFiles.List(AppPaths.LogsDir);
        foreach (var file in files)
        {
            Files.Add(file);
        }

        LogsDirAvailable = Directory.Exists(AppPaths.LogsDir);
        OpenFolderPath = LogsDirAvailable ? AppPaths.LogsDir : AppPaths.DataDir;
        CanFollow = Files.Count > 0;

        if (!LogsDirAvailable)
        {
            ShowBanner(
                InfoBarSeverity.Warning,
                "日志目录不存在",
                $"未找到 {AppPaths.LogsDir}（服务可能尚未安装或从未运行）。");
            ClearEntries();
            SelectedFile = null;
            return;
        }

        var match = SelectedFile is null
            ? null
            : Files.FirstOrDefault(f => string.Equals(f.FullPath, SelectedFile.FullPath, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            SelectedFile = match; // 同一路径的新实例 → 触发重新加载
        }
        else if (Files.Count > 0)
        {
            DismissBanner();
            SelectedFile = Files[0];
        }
        else
        {
            ClearEntries();
            SelectedFile = null;
            ShowBanner(
                InfoBarSeverity.Informational,
                "暂无日志文件",
                "目录中还没有 autounlock-*.log 文件，服务启动后将自动出现。");
        }
    }

    /// <summary>暂停（冻结显示）；继续时从暂停点增量补读并追上（含暂停期间换日则切到最新）。</summary>
    public void TogglePause()
    {
        IsPaused = !IsPaused;
        if (IsPaused)
        {
            StopLoop();
            UpdateStatusText();
            return;
        }

        if (SelectedFile is { } file)
        {
            var newest = Files.Count > 0 ? Files[0] : null;
            if (_followNewest && newest is not null &&
                !string.Equals(newest.FullPath, file.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                SelectedFile = newest; // 触发重新加载
                return;
            }

            StartLoop(file.FullPath, _position, _carry);
            UpdateStatusText();
        }
    }

    /// <summary>停止跟随（页面导航离开/窗口关闭）。</summary>
    public void Stop()
    {
        StopLoop();
        _keywordTimer.Stop();
    }

    /// <summary>导出当前过滤视图（原文行，\n 连接、含尾换行）。</summary>
    public string BuildExportText()
    {
        var builder = new StringBuilder();
        foreach (var entry in VisibleEntries)
        {
            builder.Append(entry.Raw).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>显示横幅。</summary>
    public void ShowBanner(InfoBarSeverity severity, string title, string message)
    {
        BannerSeverity = severity;
        BannerTitle = title;
        BannerMessage = message;
        BannerVisible = true;
    }

    /// <summary>隐藏横幅。</summary>
    public void DismissBanner() => BannerVisible = false;

    // —— 属性变更联动 ——

    partial void OnSelectedFileChanged(LogFileInfo? oldValue, LogFileInfo? newValue)
    {
        if (ReferenceEquals(oldValue, newValue))
        {
            return;
        }

        _followNewest = newValue is not null && Files.Count > 0 &&
                        string.Equals(newValue.FullPath, Files[0].FullPath, StringComparison.OrdinalIgnoreCase);
        if (newValue is not null)
        {
            _ = LoadAndFollowAsync(newValue.FullPath);
        }
    }

    partial void OnIsPausedChanged(bool value) => OnPropertyChanged(nameof(PauseButtonText));

    partial void OnKeywordChanged(string value)
    {
        _keywordTimer.Stop();
        _keywordTimer.Start(); // 防抖后重建
    }

    partial void OnShowDebugChanged(bool value) => RebuildVisible();

    partial void OnShowInformationChanged(bool value) => RebuildVisible();

    partial void OnShowWarningChanged(bool value) => RebuildVisible();

    partial void OnShowErrorChanged(bool value) => RebuildVisible();

    // —— 加载与跟随 ——

    private async Task LoadAndFollowAsync(string path)
    {
        StopLoop();
        try
        {
            var (read, entries, carry) = await Task.Run(() =>
            {
                var source = LogFollower.ReadAll(path);
                var (parsed, carry) = LogLineParser.ParseBatch(source.Lines, LogSeverity.Unknown);
                return (source, parsed, carry);
            });

            _all = entries;
            _carry = carry;
            _fileMissingNotified = false;
            IsPaused = false;
            RebuildVisible();
            StartLoop(path, read.ConsumedBytes, carry);
        }
        catch (Exception ex)
        {
            ShowBanner(InfoBarSeverity.Error, "加载日志失败", ex.Message);
        }
    }

    private void StartLoop(string path, long position, LogSeverity carry)
    {
        var cts = new CancellationTokenSource();
        lock (_followGate)
        {
            _cts = cts;
            _position = position;
        }

        _ = Task.Run(() => FollowLoopAsync(path, position, carry, cts.Token), cts.Token);
    }

    private void StopLoop()
    {
        CancellationTokenSource? cts;
        lock (_followGate)
        {
            cts = _cts;
            _cts = null;
        }

        try
        {
            cts?.Cancel();
            cts?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // 已在别处处置
        }
    }

    private async Task FollowLoopAsync(string path, long startPosition, LogSeverity carry, CancellationToken ct)
    {
        var follower = new LogFollower(path, startPosition);
        var rotateDue = DateTimeOffset.UtcNow + RotateCheckInterval;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = follower.ReadNew();
                lock (_followGate)
                {
                    _position = follower.Position;
                }

                if (result.FileMissing)
                {
                    NotifyFileMissing(path);
                }
                else if (result.Lines.Count > 0)
                {
                    var (entries, newCarry) = LogLineParser.ParseBatch(result.Lines, carry);
                    carry = newCarry;
                    _ = _dispatcher.BeginInvoke(() => AppendEntries(entries));
                }

                if (DateTimeOffset.UtcNow >= rotateDue)
                {
                    rotateDue = DateTimeOffset.UtcNow + RotateCheckInterval;
                    CheckRotation(path);
                }

                await Task.Delay(PollInterval, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            _ = _dispatcher.BeginInvoke(() => ShowBanner(InfoBarSeverity.Error, "日志跟随中断", ex.Message));
        }
    }

    /// <summary>换日侦测（后台线程）：只在仍选中该文件且关注最新时提请切换。</summary>
    private void CheckRotation(string path)
    {
        if (!_followNewest || IsPaused)
        {
            return;
        }

        var files = LogFiles.List(AppPaths.LogsDir);
        if (files.Count == 0 ||
            string.Equals(files[0].FullPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _ = _dispatcher.BeginInvoke(() =>
        {
            if (IsPaused || SelectedFile is null ||
                !string.Equals(SelectedFile.FullPath, path, StringComparison.OrdinalIgnoreCase))
            {
                return; // 期间用户已手动切换/暂停
            }

            var files = LogFiles.List(AppPaths.LogsDir);
            if (files.Count == 0 ||
                string.Equals(files[0].FullPath, path, StringComparison.OrdinalIgnoreCase))
            {
                return; // 新文件又消失：保持现状
            }

            Files.Clear();
            foreach (var file in files)
            {
                Files.Add(file);
            }

            CanFollow = true;
            SelectedFile = Files[0]; // 触发重新加载 + 跟随（followNewest 自动为 true）
            ShowBanner(
                InfoBarSeverity.Informational,
                "已切换日志文件",
                $"服务日志已轮转到 {Files[0].FileName}。");
        });
    }

    private void NotifyFileMissing(string path)
    {
        if (_fileMissingNotified || IsPaused)
        {
            return;
        }

        _ = _dispatcher.BeginInvoke(() =>
        {
            if (!_fileMissingNotified && SelectedFile is not null &&
                string.Equals(SelectedFile.FullPath, path, StringComparison.OrdinalIgnoreCase))
            {
                _fileMissingNotified = true;
                ShowBanner(
                    InfoBarSeverity.Warning,
                    "日志文件不可读",
                    $"{Path.GetFileName(path)} 当前无法读取（可能正被轮转）；恢复写入后自动继续。");
            }
        });
    }

    // —— 行追加 / 过滤 / 裁剪（UI 线程） ——

    private void AppendEntries(List<LogEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var levels = CurrentLevelFilter();
        var counts = Counts;
        var added = 0;
        foreach (var entry in entries)
        {
            _all.Add(entry);
            counts = counts.Add(entry);
            if (LogFilter.Matches(entry, levels, Keyword))
            {
                VisibleEntries.Add(entry);
                added++;
            }
        }

        Counts = counts;
        if (!IsFollowing)
        {
            PendingNewLines += added;
        }

        PruneIfNeeded();
        UpdateStatusText();
    }

    private void PruneIfNeeded()
    {
        if (_all.Count <= MaxLineCount)
        {
            return;
        }

        var pruneCount = Math.Max(1, MaxLineCount / 10);
        while (_all.Count > MaxLineCount)
        {
            _all.RemoveRange(0, Math.Min(pruneCount, _all.Count));
        }

        RebuildVisible(); // 过滤视图整体重放（低频：每 10% 上限一次）
    }

    private LogLevelFilter CurrentLevelFilter() => new()
    {
        Debug = ShowDebug,
        Information = ShowInformation,
        Warning = ShowWarning,
        Error = ShowError,
    };

    /// <summary>按当前过滤条件重建过滤视图（替换 ItemsSource）。</summary>
    public void RebuildVisible()
    {
        VisibleEntries = new ObservableCollection<LogEntry>(LogFilter.Apply(_all, CurrentLevelFilter(), Keyword));
        Counts = LogLevelCounts.From(_all);
        PendingNewLines = 0;
        UpdateStatusText();
        ViewRebuilt?.Invoke();
    }

    private void ClearEntries()
    {
        _all = [];
        VisibleEntries = new ObservableCollection<LogEntry>();
        Counts = LogLevelCounts.Empty;
        PendingNewLines = 0;
        UpdateStatusText();
    }

    private void UpdateStatusText()
    {
        var file = SelectedFile?.FileName ?? "（无文件）";
        var marker = IsPaused ? "已暂停" : _followNewest ? "跟随中" : "浏览";
        StatusText = $"{file}｜{marker}｜显示 {VisibleEntries.Count.ToString("N0", CultureInfo.InvariantCulture)} / " +
                     $"共 {_all.Count.ToString("N0", CultureInfo.InvariantCulture)} 行";
        LineCountText = $"显示 {VisibleEntries.Count.ToString("N0", CultureInfo.InvariantCulture)} 行，" +
                        $"共 {_all.Count.ToString("N0", CultureInfo.InvariantCulture)} 行";
    }
}

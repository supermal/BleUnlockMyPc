using System.ComponentModel;
using AutoUnlockManager.Core;
using AutoUnlockManager.Core.Config;
using AutoUnlockManager.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;

namespace AutoUnlockManager.ViewModels;

/// <summary>保存准备结果：<c>Proceed=true</c> 时 <c>Changes</c> 为确认对话框要展示的差异列表。</summary>
public sealed record SavePreparation(bool Proceed, IReadOnlyList<ConfigChange> Changes);

/// <summary>横幅动作按钮类别。</summary>
public enum ConfigBannerAction
{
    None,
    StartService,
    RestartService,
}

/// <summary>
/// 配置页视图模型（设计 §5.3 配置页）：全字段表单 + 校验（镜像服务端）+ 保存流水线
/// （差异确认 → 原子写盘 → 服务热重载日志确认）+ 辅助操作（重新加载/打开文件夹/服务启停）。
/// 全部逻辑在 UI 线程（无后台事件）；服务控制复用 <see cref="ServiceControlService"/>（后台线程执行）。
/// </summary>
public sealed partial class ConfigViewModel : ObservableObject
{
    private static readonly TimeSpan ReloadWaitTimeout = TimeSpan.FromSeconds(5);

    private readonly ServiceControlService _serviceControl = new();

    /// <summary>最近一次成功读盘/写盘的快照（差异与「无更改」判定基准）。</summary>
    private AutoUnlockConfig _loadedConfig = new();

    /// <summary>已通过校验与确认、等待写入的配置。</summary>
    private AutoUnlockConfig? _pendingConfig;

    private ConfigBannerAction _bannerAction = ConfigBannerAction.None;
    private bool _serviceOperationInProgress;

    // —— 可用性 / 路径 ——

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial bool CanEdit { get; set; }

    [ObservableProperty] public partial bool CanOpenFolder { get; set; }
    [ObservableProperty] public partial string ConfigPathText { get; set; }
    [ObservableProperty] public partial bool PathWarningVisible { get; set; }
    [ObservableProperty] public partial string PathWarningText { get; set; }

    // —— 横幅 / 保存状态 ——

    [ObservableProperty] public partial bool BannerVisible { get; set; }
    [ObservableProperty] public partial InfoBarSeverity BannerSeverity { get; set; }
    [ObservableProperty] public partial string BannerTitle { get; set; }
    [ObservableProperty] public partial string BannerMessage { get; set; }
    [ObservableProperty] public partial bool BannerActionVisible { get; set; }
    [ObservableProperty] public partial string BannerActionText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial bool IsSaving { get; set; }

    /// <summary>保存按钮可用：可编辑且当前无保存进行中。</summary>
    public bool CanSave => CanEdit && !IsSaving;

    /// <summary>横幅动作按钮类别（代码隐藏读取后分发）。</summary>
    public ConfigBannerAction BannerActionKind => _bannerAction;

    // —— 表单字段 ——

    [ObservableProperty] public partial string IrkText { get; set; }
    [ObservableProperty] public partial int IrkByteOrderIndex { get; set; }
    [ObservableProperty] public partial string IdentityAddressText { get; set; }
    [ObservableProperty] public partial string UserNameText { get; set; }
    [ObservableProperty] public partial string PasswordText { get; set; }
    [ObservableProperty] public partial string LockThresholdText { get; set; }
    [ObservableProperty] public partial string UnlockThresholdText { get; set; }
    [ObservableProperty] public partial string UnlockCountText { get; set; }
    [ObservableProperty] public partial string LockPromptCountText { get; set; }
    [ObservableProperty] public partial string LockConfirmCountText { get; set; }
    [ObservableProperty] public partial string StartupGraceText { get; set; }
    [ObservableProperty] public partial string UnlockPendingTimeoutText { get; set; }
    [ObservableProperty] public partial int NotifierModeIndex { get; set; }
    [ObservableProperty] public partial string RejectSnoozeText { get; set; }
    [ObservableProperty] public partial string SnoozeText { get; set; }

    // —— 字段级错误（空串 = 无错误） ——

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIrkError))]
    public partial string IrkError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIdentityAddressError))]
    public partial string IdentityAddressError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUserNameError))]
    public partial string UserNameError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPasswordError))]
    public partial string PasswordError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLockThresholdError))]
    public partial string LockThresholdError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnlockThresholdError))]
    public partial string UnlockThresholdError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnlockCountError))]
    public partial string UnlockCountError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLockPromptCountError))]
    public partial string LockPromptCountError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLockConfirmCountError))]
    public partial string LockConfirmCountError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStartupGraceError))]
    public partial string StartupGraceError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnlockPendingTimeoutError))]
    public partial string UnlockPendingTimeoutError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRejectSnoozeError))]
    public partial string RejectSnoozeError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSnoozeError))]
    public partial string SnoozeError { get; set; }

    public bool HasIrkError => IrkError.Length > 0;
    public bool HasIdentityAddressError => IdentityAddressError.Length > 0;
    public bool HasUserNameError => UserNameError.Length > 0;
    public bool HasPasswordError => PasswordError.Length > 0;
    public bool HasLockThresholdError => LockThresholdError.Length > 0;
    public bool HasUnlockThresholdError => UnlockThresholdError.Length > 0;
    public bool HasUnlockCountError => UnlockCountError.Length > 0;
    public bool HasLockPromptCountError => LockPromptCountError.Length > 0;
    public bool HasLockConfirmCountError => LockConfirmCountError.Length > 0;
    public bool HasStartupGraceError => StartupGraceError.Length > 0;
    public bool HasUnlockPendingTimeoutError => UnlockPendingTimeoutError.Length > 0;
    public bool HasRejectSnoozeError => RejectSnoozeError.Length > 0;
    public bool HasSnoozeError => SnoozeError.Length > 0;

    // —— 敏感字段存储提示（空串 = 无提示；已加密/明文/不可解密三种形态） ——

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIrkHint))]
    public partial string IrkHint { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPasswordHint))]
    public partial string PasswordHint { get; set; }

    public bool HasIrkHint => IrkHint.Length > 0;
    public bool HasPasswordHint => PasswordHint.Length > 0;

    public ConfigViewModel()
    {
        // partial 属性的初始值在构造函数设置（生成器不支持声明处初始化）
        ConfigPathText = "";
        PathWarningText = "";
        BannerTitle = "";
        BannerMessage = "";
        BannerActionText = "";
        IrkText = "";
        IdentityAddressText = "";
        UserNameText = "";
        PasswordText = "";
        LockThresholdText = "";
        UnlockThresholdText = "";
        UnlockCountText = "";
        LockPromptCountText = "";
        LockConfirmCountText = "";
        StartupGraceText = "";
        UnlockPendingTimeoutText = "";
        RejectSnoozeText = "";
        SnoozeText = "";
        IrkError = "";
        IdentityAddressError = "";
        UserNameError = "";
        PasswordError = "";
        LockThresholdError = "";
        UnlockThresholdError = "";
        UnlockCountError = "";
        LockPromptCountError = "";
        LockConfirmCountError = "";
        StartupGraceError = "";
        UnlockPendingTimeoutError = "";
        RejectSnoozeError = "";
        SnoozeError = "";
        IrkHint = "";
        PasswordHint = "";
    }

    /// <summary>页面进入时调用：记录路径并首次加载。</summary>
    public void Initialize()
    {
        ConfigPathText = AppPaths.ConfigFile;
        ReloadFromDisk();
    }

    /// <summary>表单当前内容与最近快照的差异（含无法解析的输入，视为已修改）。</summary>
    public bool IsDirty
    {
        get
        {
            var (config, errors) = BuildFromForm();
            return errors.Count > 0 || ConfigDiff.Compute(_loadedConfig, config).Count > 0;
        }
    }

    /// <summary>
    /// 重新从磁盘加载（丢弃表单编辑）。数据目录/文件缺失或解析失败 → 路径警告 + 禁用编辑。
    /// </summary>
    public void ReloadFromDisk()
    {
        ClearFieldErrors();
        _pendingConfig = null;
        BannerVisible = false; // 清掉上一次操作的陈旧的横幅

        try
        {
            if (!Directory.Exists(AppPaths.DataDir))
            {
                SetUnavailable("未检测到服务数据目录（服务可能未安装）。");
                return;
            }

            if (!File.Exists(AppPaths.ConfigFile))
            {
                SetUnavailable("未找到配置文件 appsettings.json（服务可能未安装或尚未初始化）。");
                return;
            }

            _loadedConfig = ConfigDocument.Load(AppPaths.ConfigFile);
            ApplyToFields(_loadedConfig);
            CanEdit = true;
            CanOpenFolder = true;
            PathWarningVisible = false;

            // 加载即校验（设计前提：配置非法时服务拒绝启动/保留原配置）：先于保存暴露问题（评审建议 3）
            var validation = ConfigValidator.Validate(_loadedConfig);
            if (!validation.IsValid)
            {
                ApplyFieldErrors(validation.Errors);
                ShowBanner(InfoBarSeverity.Warning, "当前配置未通过校验",
                    $"共 {validation.Errors.Count} 处问题，服务在修正前会拒绝启动（下方字段已标出）。");
            }

            // 跨机器拷贝等导致的不可解密密文：服务会拒绝启动，这里提前给出可操作提示（覆盖校验横幅）
            if (ConfigSecrets.IsUnreadableOnThisMachine(_loadedConfig.Device.Irk) ||
                ConfigSecrets.IsUnreadableOnThisMachine(_loadedConfig.Credentials.Password))
            {
                ShowBanner(InfoBarSeverity.Warning, "检测到无法解密的加密值",
                    "设备 IRK 或凭据密码包含无法在本机解密的密文（可能来自其他机器）；请重新输入并保存，否则服务会拒绝启动。");
            }
        }
        catch (Exception ex)
        {
            SetUnavailable($"无法读取配置文件：{DescribeException(ex)}");
        }
    }

    /// <summary>
    /// 保存准备：本地校验（解析 + 规则镜像）→ 差异计算。
    /// 校验失败/无更改时直接显示横幅并返回 <c>Proceed=false</c>；通过时返回差异列表供确认对话框展示。
    /// </summary>
    public SavePreparation PrepareSave()
    {
        ClearFieldErrors();

        var (config, parseErrors) = BuildFromForm();

        // 解析失败字段不再附带基于默认值的规则错误（避免噪声）；其余字段的规则错误一次呈现
        var brokenFields = new HashSet<string>(parseErrors.Select(e => e.Field), StringComparer.Ordinal);
        var errors = new List<ConfigError>(parseErrors);
        errors.AddRange(ConfigValidator.Validate(config).Errors.Where(e => !brokenFields.Contains(e.Field)));

        if (errors.Count > 0)
        {
            ApplyFieldErrors(errors);
            ShowBanner(InfoBarSeverity.Error, "校验未通过，未保存",
                $"共有 {errors.Count} 处问题，请修正下方标红的字段后重试。");
            return new SavePreparation(false, []);
        }

        // 归一化写入值（与 ConfigDocument.Save 一致）：表单回填、差异比较与磁盘内容严格一致
        config.Device.Irk = ConfigValidator.NormalizeIrk(config.Device.Irk) ?? config.Device.Irk;
        var normalizedAddress = ConfigValidator.NormalizeAddress(config.Device.IdentityAddress);
        config.Device.IdentityAddress = normalizedAddress is null
            ? null
            : ConfigValidator.FormatAddress(normalizedAddress);

        var changes = ConfigDiff.Compute(_loadedConfig, config);
        if (changes.Count == 0)
        {
            ShowBanner(InfoBarSeverity.Informational, "没有需要保存的更改", "当前表单内容与配置文件一致。");
            return new SavePreparation(false, []);
        }

        // 保存前把新输入的明文加密（密文原样保留、空值跳过）；差异列表已按明文计算
        ConfigSecrets.ProtectNewValues(config);
        _pendingConfig = config;
        return new SavePreparation(true, changes);
    }

    /// <summary>
    /// 保存流水线（确认对话框通过后调用）：记录日志检查点 → 原子写盘（.bak）→
    /// 服务运行中则等待热重载日志确认（5 秒），未运行则提示启动后生效。
    /// </summary>
    public async Task SaveAsync()
    {
        var config = _pendingConfig;
        if (IsSaving || config is null)
        {
            return;
        }

        _pendingConfig = null;
        IsSaving = true;

        try
        {
            var checkpoint = ConfigReloadWatcher.CaptureCheckpoint(AppPaths.LogsDir);
            ConfigDocument.Save(AppPaths.ConfigFile, config);

            // 写盘成功后：文件真值更新（表单回填归一化结果）
            _loadedConfig = config;
            ApplyToFields(config);
            ClearFieldErrors();

            ServiceStatusInfo status;
            try
            {
                status = await _serviceControl.GetStatusAsync();
            }
            catch
            {
                status = new ServiceStatusInfo(ServiceStatusKind.Unknown);
            }

            if (status.Kind is not ServiceStatusKind.Running)
            {
                var message = status.Kind == ServiceStatusKind.Stopped
                    ? "服务当前已停止，新配置将在服务启动后生效。"
                    : "无法确认服务正在运行，新配置将在服务运行后生效。";
                ShowBanner(InfoBarSeverity.Success, "配置已保存", message,
                    ConfigBannerAction.StartService, "启动服务");
                return;
            }

            ShowBanner(InfoBarSeverity.Informational, "配置已写入",
                "正在等待服务确认热重载（最多 5 秒）…");

            var outcome = await ConfigReloadWatcher.WaitAsync(AppPaths.LogsDir, checkpoint, ReloadWaitTimeout);
            switch (outcome.Status)
            {
                case ConfigReloadStatus.Reloaded:
                    ShowBanner(InfoBarSeverity.Success, "配置已重载", BuildReloadedMessage(outcome));
                    break;

                case ConfigReloadStatus.Rejected:
                    ShowBanner(InfoBarSeverity.Error, "服务拒绝了新配置（已保留原配置）",
                        outcome.Message ?? "配置校验失败，已保留原配置。");
                    break;

                default:
                    ShowBanner(InfoBarSeverity.Warning, "未确认重载",
                        "5 秒内未在日志中看到重载结果（服务可能未运行或日志暂不可用）。文件已写入；" +
                        "如服务正在运行，可重启服务使其生效。",
                        ConfigBannerAction.RestartService, "重启服务");
                    break;
            }
        }
        catch (Exception ex)
        {
            ShowBanner(InfoBarSeverity.Error, "保存失败", DescribeException(ex));
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>执行横幅动作（启动/重启服务，确认对话框由页面负责）。</summary>
    public async Task RunBannerActionAsync(ConfigBannerAction action)
    {
        if (_serviceOperationInProgress || action == ConfigBannerAction.None)
        {
            return;
        }

        _serviceOperationInProgress = true;
        BannerActionVisible = false;
        _bannerAction = ConfigBannerAction.None;

        ServiceOperationResult result;
        try
        {
            result = action == ConfigBannerAction.StartService
                ? await _serviceControl.StartAsync()
                : await _serviceControl.RestartAsync();
        }
        catch (Exception ex)
        {
            result = new ServiceOperationResult(ServiceOperationStatus.Failure, ex.Message);
        }
        finally
        {
            _serviceOperationInProgress = false;
        }

        var succeededTitle = action == ConfigBannerAction.StartService ? "服务已启动" : "服务已重启";
        var succeededMessage = action == ConfigBannerAction.StartService
            ? "服务已启动，磁盘上的最新配置已生效。"
            : "服务已按磁盘上的最新配置重启。";

        switch (result.Status)
        {
            case ServiceOperationStatus.Success:
                ShowBanner(InfoBarSeverity.Success, succeededTitle, succeededMessage);
                break;
            case ServiceOperationStatus.NotInstalled:
                ShowBanner(InfoBarSeverity.Error, "服务未安装", result.Message ?? "服务未安装。");
                break;
            case ServiceOperationStatus.AccessDenied:
                // 保留同一动作按钮：权限/瞬态问题修正后可直接重试（评审 M6）
                ShowBanner(InfoBarSeverity.Error, "拒绝访问", result.Message ?? "需要管理员权限。",
                    action, ActionText(action));
                break;
            case ServiceOperationStatus.Timeout:
                ShowBanner(InfoBarSeverity.Warning, "操作超时", result.Message ?? "等待服务状态超时。",
                    action, ActionText(action));
                break;
            default:
                ShowBanner(InfoBarSeverity.Error, "操作失败", result.Message ?? "未知错误。",
                    action, ActionText(action));
                break;
        }
    }

    /// <summary>动作按钮文案（横幅动作与失败重试路径共用）。</summary>
    private static string ActionText(ConfigBannerAction action)
        => action == ConfigBannerAction.StartService ? "启动服务" : "重启服务";

    /// <summary>显示横幅（页面代码隐藏的辅助错误也复用）。</summary>
    public void ShowBanner(InfoBarSeverity severity, string title, string message,
        ConfigBannerAction action = ConfigBannerAction.None, string actionText = "")
    {
        BannerSeverity = severity;
        BannerTitle = title;
        BannerMessage = message;
        BannerVisible = true;
        _bannerAction = action;
        BannerActionText = actionText;
        BannerActionVisible = action != ConfigBannerAction.None;
    }

    /// <summary>显示错误横幅。</summary>
    public void ShowError(string title, string message)
        => ShowBanner(InfoBarSeverity.Error, title, message);

    // —— 表单 ↔ 配置 ——

    private (AutoUnlockConfig Config, List<ConfigError> Errors) BuildFromForm()
    {
        var config = new AutoUnlockConfig();
        var errors = new List<ConfigError>();

        // 敏感字段：表单显示当前值（磁盘密文已在 ApplyToFields 解密回显）；
        // 直接用表单文本构建，保存时由 ProtectNewValues 重新加密
        config.Device.Irk = IrkText.Trim();
        config.Device.IrkByteOrder = (IrkByteOrder)Math.Clamp(IrkByteOrderIndex, 0, 2);
        config.Device.IdentityAddress = string.IsNullOrWhiteSpace(IdentityAddressText)
            ? null
            : IdentityAddressText.Trim();
        config.Credentials.UserName = UserNameText;
        config.Credentials.Password = PasswordText;

        var p = config.Proximity;
        if (TryGetNumber(LockThresholdText, ConfigFields.ProximityLockThresholdDbm, errors, out var lockThreshold))
            p.LockThresholdDbm = lockThreshold;
        if (TryGetNumber(UnlockThresholdText, ConfigFields.ProximityUnlockThresholdDbm, errors, out var unlockThreshold))
            p.UnlockThresholdDbm = unlockThreshold;
        if (TryGetCount(UnlockCountText, ConfigFields.ProximityUnlockSampleCount, errors, out var unlockCount))
            p.UnlockSampleCount = unlockCount;
        if (TryGetCount(LockPromptCountText, ConfigFields.ProximityLockPromptSampleCount, errors, out var lockPromptCount))
            p.LockPromptSampleCount = lockPromptCount;
        if (TryGetCount(LockConfirmCountText, ConfigFields.ProximityLockConfirmSampleCount, errors, out var lockConfirmCount))
            p.LockConfirmSampleCount = lockConfirmCount;
        if (TryGetNumber(StartupGraceText, ConfigFields.ProximityStartupGraceSeconds, errors, out var startupGrace))
            p.StartupGraceSeconds = startupGrace;
        if (TryGetNumber(UnlockPendingTimeoutText, ConfigFields.ProximityUnlockPendingTimeoutSeconds, errors, out var unlockPending))
            p.UnlockPendingTimeoutSeconds = unlockPending;

        var n = config.Notification;
        n.Mode = (NotifierMode)Math.Clamp(NotifierModeIndex, 0, 1);
        if (TryGetNumber(RejectSnoozeText, ConfigFields.NotificationRejectSnoozeSeconds, errors, out var rejectSnooze))
            n.RejectSnoozeSeconds = rejectSnooze;
        if (TryGetNumber(SnoozeText, ConfigFields.NotificationSnoozeMinutes, errors, out var snooze))
            n.SnoozeMinutes = snooze;

        return (config, errors);
    }

    private void ApplyToFields(AutoUnlockConfig config)
    {
        IrkText = ConfigSecrets.DisplayText(config.Device.Irk);
        IrkByteOrderIndex = Math.Clamp((int)config.Device.IrkByteOrder, 0, 2);
        IdentityAddressText = config.Device.IdentityAddress ?? "";
        UserNameText = config.Credentials.UserName;
        PasswordText = ConfigSecrets.DisplayText(config.Credentials.Password);
        LockThresholdText = ConfigInput.FormatNumber(config.Proximity.LockThresholdDbm);
        UnlockThresholdText = ConfigInput.FormatNumber(config.Proximity.UnlockThresholdDbm);
        UnlockCountText = ConfigInput.FormatCount(config.Proximity.UnlockSampleCount);
        LockPromptCountText = ConfigInput.FormatCount(config.Proximity.LockPromptSampleCount);
        LockConfirmCountText = ConfigInput.FormatCount(config.Proximity.LockConfirmSampleCount);
        StartupGraceText = ConfigInput.FormatNumber(config.Proximity.StartupGraceSeconds);
        UnlockPendingTimeoutText = ConfigInput.FormatNumber(config.Proximity.UnlockPendingTimeoutSeconds);
        NotifierModeIndex = Math.Clamp((int)config.Notification.Mode, 0, 1);
        RejectSnoozeText = ConfigInput.FormatNumber(config.Notification.RejectSnoozeSeconds);
        SnoozeText = ConfigInput.FormatNumber(config.Notification.SnoozeMinutes);
        IrkHint = ConfigSecrets.HintText(config.Device.Irk);
        PasswordHint = ConfigSecrets.HintText(config.Credentials.Password);
    }

    private static bool TryGetNumber(string text, string field, List<ConfigError> errors, out double value)
    {
        if (ConfigInput.TryParseNumber(text, out value))
        {
            return true;
        }

        errors.Add(new ConfigError(field, $"「{ConfigDiff.GetDisplayName(field)}」必须是数字"));
        return false;
    }

    private static bool TryGetCount(string text, string field, List<ConfigError> errors, out int value)
    {
        if (ConfigInput.TryParseCount(text, out value))
        {
            return true;
        }

        errors.Add(new ConfigError(field, $"「{ConfigDiff.GetDisplayName(field)}」必须是整数"));
        return false;
    }

    // —— 错误映射 / 清理 ——

    private void ApplyFieldErrors(IReadOnlyList<ConfigError> errors)
    {
        foreach (var error in errors)
        {
            SetFieldError(error);
        }
    }

    private void SetFieldError(ConfigError error)
    {
        // 同一字段多条错误时保留第一条（服务端消息顺序即有意义的优先序）
        switch (error.Field)
        {
            case ConfigFields.DeviceIrk:
                if (IrkError.Length == 0) IrkError = error.Message;
                break;
            case ConfigFields.DeviceIdentityAddress:
                if (IdentityAddressError.Length == 0) IdentityAddressError = error.Message;
                break;
            case ConfigFields.CredentialsUserName:
                if (UserNameError.Length == 0) UserNameError = error.Message;
                break;
            case ConfigFields.CredentialsPassword:
                if (PasswordError.Length == 0) PasswordError = error.Message;
                break;
            case ConfigFields.ProximityLockThresholdDbm:
                if (LockThresholdError.Length == 0) LockThresholdError = error.Message;
                break;
            case ConfigFields.ProximityUnlockThresholdDbm:
                if (UnlockThresholdError.Length == 0) UnlockThresholdError = error.Message;
                break;
            case ConfigFields.ProximityUnlockSampleCount:
                if (UnlockCountError.Length == 0) UnlockCountError = error.Message;
                break;
            case ConfigFields.ProximityLockPromptSampleCount:
                if (LockPromptCountError.Length == 0) LockPromptCountError = error.Message;
                break;
            case ConfigFields.ProximityLockConfirmSampleCount:
                if (LockConfirmCountError.Length == 0) LockConfirmCountError = error.Message;
                break;
            case ConfigFields.ProximityStartupGraceSeconds:
                if (StartupGraceError.Length == 0) StartupGraceError = error.Message;
                break;
            case ConfigFields.ProximityUnlockPendingTimeoutSeconds:
                if (UnlockPendingTimeoutError.Length == 0) UnlockPendingTimeoutError = error.Message;
                break;
            case ConfigFields.NotificationRejectSnoozeSeconds:
                if (RejectSnoozeError.Length == 0) RejectSnoozeError = error.Message;
                break;
            case ConfigFields.NotificationSnoozeMinutes:
                if (SnoozeError.Length == 0) SnoozeError = error.Message;
                break;
            default:
                break; // IrkByteOrder / Notification.Mode 无输入型错误
        }
    }

    private void ClearFieldErrors()
    {
        IrkError = "";
        IdentityAddressError = "";
        UserNameError = "";
        PasswordError = "";
        LockThresholdError = "";
        UnlockThresholdError = "";
        UnlockCountError = "";
        LockPromptCountError = "";
        LockConfirmCountError = "";
        StartupGraceError = "";
        UnlockPendingTimeoutError = "";
        RejectSnoozeError = "";
        SnoozeError = "";
        IrkHint = "";
        PasswordHint = "";
    }

    /// <summary>用户编辑某字段时清除其错误提示（错误仅在下次保存/重载时重新计算）。</summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        switch (e.PropertyName)
        {
            case nameof(IrkText):
                if (IrkError is { Length: > 0 }) IrkError = "";
                break;
            case nameof(IdentityAddressText):
                if (IdentityAddressError is { Length: > 0 }) IdentityAddressError = "";
                break;
            case nameof(UserNameText):
                if (UserNameError is { Length: > 0 }) UserNameError = "";
                break;
            case nameof(PasswordText):
                if (PasswordError is { Length: > 0 }) PasswordError = "";
                break;
            case nameof(LockThresholdText):
                if (LockThresholdError is { Length: > 0 }) LockThresholdError = "";
                break;
            case nameof(UnlockThresholdText):
                if (UnlockThresholdError is { Length: > 0 }) UnlockThresholdError = "";
                break;
            case nameof(UnlockCountText):
                if (UnlockCountError is { Length: > 0 }) UnlockCountError = "";
                break;
            case nameof(LockPromptCountText):
                if (LockPromptCountError is { Length: > 0 }) LockPromptCountError = "";
                break;
            case nameof(LockConfirmCountText):
                if (LockConfirmCountError is { Length: > 0 }) LockConfirmCountError = "";
                break;
            case nameof(StartupGraceText):
                if (StartupGraceError is { Length: > 0 }) StartupGraceError = "";
                break;
            case nameof(UnlockPendingTimeoutText):
                if (UnlockPendingTimeoutError is { Length: > 0 }) UnlockPendingTimeoutError = "";
                break;
            case nameof(RejectSnoozeText):
                if (RejectSnoozeError is { Length: > 0 }) RejectSnoozeError = "";
                break;
            case nameof(SnoozeText):
                if (SnoozeError is { Length: > 0 }) SnoozeError = "";
                break;
            default:
                break;
        }
    }

    private void SetUnavailable(string warning)
    {
        CanEdit = false;
        CanOpenFolder = Directory.Exists(AppPaths.DataDir);
        PathWarningVisible = true;
        PathWarningText = $"{warning} 配置路径：{AppPaths.ConfigFile}。修复后请点「重新从磁盘加载」。";
    }

    private static string BuildReloadedMessage(ConfigReloadOutcome outcome)
    {
        if (outcome.DeviceChanged is null && outcome.CredentialsChanged is null)
        {
            return "服务已应用新配置。";
        }

        var device = outcome.DeviceChanged == true ? "是" : "否";
        var credentials = outcome.CredentialsChanged == true ? "是" : "否";
        return $"服务已应用新配置（设备变更：{device}，凭据变更：{credentials}）。";
    }

    private static string DescribeException(Exception ex) => ex switch
    {
        System.Text.Json.JsonException => $"JSON 格式错误（{ex.Message}）",
        UnauthorizedAccessException => $"没有访问权限（{ex.Message}）",
        IOException => ex.Message,
        _ => ex.Message,
    };
}

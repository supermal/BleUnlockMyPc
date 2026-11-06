using System.Collections.ObjectModel;
using System.ComponentModel;
using BleUnlockManager.Core;
using BleUnlockManager.Core.Bonds;
using BleUnlockManager.Core.Config;
using BleUnlockManager.Core.Localization;
using BleUnlockManager.Services;
using BleUnlockManager.Services.Ble;
using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;
using BleUnlockManager.Core.Resources;

namespace BleUnlockManager.ViewModels;

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
/// 设备 IRK 不再手工输入：从「已配对 BLE 设备」下拉框选择，IRK 由服务从 Windows
/// 蓝牙配对库读取（SYSTEM 权限），保存时仍按 Task 21 的 DPAPI 链路加密。
/// 全部逻辑在 UI 线程（无后台事件）；服务控制复用 <see cref="ServiceControlService"/>（后台线程执行）。
/// </summary>
public sealed partial class ConfigViewModel : ObservableObject
{
    private static readonly TimeSpan ReloadWaitTimeout = TimeSpan.FromSeconds(5);

    private static readonly HashSet<string> FormProperties =
    [
        nameof(IrkText), nameof(IrkByteOrderIndex), nameof(IdentityAddressText), nameof(UserNameText),
        nameof(PasswordText), nameof(LockThresholdText), nameof(UnlockThresholdText), nameof(UnlockCountText),
        nameof(LockPromptCountText), nameof(LockConfirmCountText), nameof(SampleIntervalText),
        nameof(StartupGraceText), nameof(UnlockPendingTimeoutText), nameof(NoSignalDepartureText), nameof(NotifierModeIndex),
        nameof(RejectSnoozeText), nameof(SnoozeText),
    ];

    private readonly ServiceControlService _serviceControl = new();
    private readonly BleBondClient _bondClient = new();
    private readonly TargetDeviceService _device;
    private readonly PasswordInput _password = new();

    /// <summary>程序化设置下拉选中项时抑制写回（避免加载即被判定为“已修改”）。</summary>
    private bool _suppressBondSelection;

    private bool _loadingBonds;

    /// <summary>最近一次成功读盘/写盘的快照（差异与「无更改」判定基准）。</summary>
    private BleUnlockConfig _loadedConfig = new();

    /// <summary>已通过校验与确认、等待写入的配置。</summary>
    private BleUnlockConfig? _pendingConfig;

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

    /// <summary>已与 Windows 配对的 BLE 设备（服务从配对库读取；可能含一条「当前配置」合成项）。</summary>
    public ObservableCollection<BleBondDevice> BondedDevices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUnpairSelectedDevice))]
    public partial BleBondDevice? SelectedBondDevice { get; set; }

    /// <summary>「解除匹配」可用：仅真实的 Windows 配对设备（合成项地址为空）。</summary>
    public bool CanUnpairSelectedDevice => SelectedBondDevice is { Address.Length: > 0, Paired: true };

    /// <summary>当前下拉选中项是否就是磁盘配置正在使用的设备（解除配对时要清空配置/停服务）。</summary>
    public bool IsSelectedDeviceActive => SelectedBondDevice is { } device && IsLoadedDevice(device);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBondStatus))]
    public partial string BondStatusText { get; set; }

    public bool HasBondStatus => BondStatusText.Length > 0;

    [ObservableProperty] public partial string UserNameText { get; set; }
    [ObservableProperty] public partial string PasswordText { get; set; }
    [ObservableProperty] public partial string LockThresholdText { get; set; }
    [ObservableProperty] public partial string UnlockThresholdText { get; set; }
    [ObservableProperty] public partial string UnlockCountText { get; set; }
    [ObservableProperty] public partial string LockPromptCountText { get; set; }
    [ObservableProperty] public partial string LockConfirmCountText { get; set; }
    [ObservableProperty] public partial string SampleIntervalText { get; set; }
    [ObservableProperty] public partial string StartupGraceText { get; set; }
    [ObservableProperty] public partial string UnlockPendingTimeoutText { get; set; }
    [ObservableProperty] public partial string NoSignalDepartureText { get; set; }
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
    [NotifyPropertyChangedFor(nameof(HasSampleIntervalError))]
    public partial string SampleIntervalError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStartupGraceError))]
    public partial string StartupGraceError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnlockPendingTimeoutError))]
    public partial string UnlockPendingTimeoutError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoSignalDepartureError))]
    public partial string NoSignalDepartureError { get; set; }

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
    public bool HasSampleIntervalError => SampleIntervalError.Length > 0;
    public bool HasStartupGraceError => StartupGraceError.Length > 0;
    public bool HasUnlockPendingTimeoutError => UnlockPendingTimeoutError.Length > 0;
    public bool HasNoSignalDepartureError => NoSignalDepartureError.Length > 0;
    public bool HasRejectSnoozeError => RejectSnoozeError.Length > 0;
    public bool HasSnoozeError => SnoozeError.Length > 0;

    // —— 敏感字段存储提示（空串 = 无提示；已加密/明文/不可解密三种形态） ——

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIrkHint))]
    public partial string IrkHint { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPasswordHint), nameof(PasswordDescription))]
    public partial string PasswordHint { get; set; }

    public bool HasIrkHint => IrkHint.Length > 0;
    public bool HasPasswordHint => PasswordHint.Length > 0;

    public ConfigViewModel(TargetDeviceService device)
    {
        _device = device;
        PendingSummary = "";
        ModifiedFields = new HashSet<string>();
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
        SampleIntervalText = "";
        StartupGraceText = "";
        UnlockPendingTimeoutText = "";
        NoSignalDepartureText = "";
        RejectSnoozeText = "";
        SnoozeText = "";
        BondStatusText = "";
        IrkError = "";
        IdentityAddressError = "";
        UserNameError = "";
        PasswordError = "";
        LockThresholdError = "";
        UnlockThresholdError = "";
        UnlockCountError = "";
        LockPromptCountError = "";
        LockConfirmCountError = "";
        SampleIntervalError = "";
        StartupGraceError = "";
        UnlockPendingTimeoutError = "";
        NoSignalDepartureError = "";
        RejectSnoozeError = "";
        SnoozeError = "";
        IrkHint = "";
        PasswordHint = "";

        ManagerLanguage.Changed += OnLanguageChanged;
    }

    /// <summary>
    /// Cambio de idioma: se rehacen los textos que calcula este modelo. Los avisos ya mostrados y la
    /// línea de estado de la lista de dispositivos se quedan como están hasta la próxima acción.
    /// </summary>
    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(PasswordPlaceholder));
        OnPropertyChanged(nameof(PasswordDescription));
        if (IrkHint.Length > 0)
        {
            IrkHint = BuildIrkHint();
        }

        if (PasswordHint.Length > 0)
        {
            PasswordHint = ConfigSecrets.HintText(_loadedConfig.Credentials.Password);
        }

        RecomputePending();
    }

    // —— Barra de cambios sin guardar ——

    /// <summary>Número de cambios pendientes (un campo que no se puede interpretar cuenta como uno).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingChanges))]
    public partial int PendingCount { get; set; }

    [ObservableProperty] public partial string PendingSummary { get; set; }

    /// <summary>Campos (ConfigFields) modificados respecto al disco: las tarjetas muestran «已修改».</summary>
    [ObservableProperty] public partial IReadOnlySet<string> ModifiedFields { get; set; }

    public bool HasPendingChanges => PendingCount > 0;

    /// <summary>El «ojo» solo aparece con una contraseña recién escrita, nunca con la guardada.</summary>
    public bool CanRevealPassword => _password.CanReveal;

    /// <summary>Texto indicativo del campo vacío: la guardada nunca se muestra, solo se indica que existe.</summary>
    public string PasswordPlaceholder => _password.HasStored ? UiStrings.Config_PasswordStored : UiStrings.Config_PasswordEnter;

    public string PasswordDescription => PasswordHint.Length > 0
        ? PasswordHint
        : UiStrings.Config_PasswordDpapiNote;

    /// <summary>Umbral de bloqueo para la banda del formulario (sigue la entrada; si no se interpreta, el del disco).</summary>
    public double LockThresholdPreview =>
        ConfigInput.TryParseNumber(LockThresholdText, out var value) ? value : _loadedConfig.Proximity.LockThresholdDbm;

    public double UnlockThresholdPreview =>
        ConfigInput.TryParseNumber(UnlockThresholdText, out var value) ? value : _loadedConfig.Proximity.UnlockThresholdDbm;

    // —— Asistente de configuración: reutiliza el mismo guardado (validación, cifrado, espera de recarga) ——

    /// <summary>Hay una contraseña guardada (el asistente permite dejar el campo vacío para conservarla).</summary>
    public bool HasStoredPassword => _password.HasStored;

    /// <summary>Lo que falta por configurar según la última copia leída o guardada.</summary>
    public SetupState SavedSetupState => SetupCheck.Evaluate(_loadedConfig);

    /// <summary>Usuario guardado (descifrado no hace falta: el usuario nunca se cifra).</summary>
    public string SavedUserName => _loadedConfig.Credentials.UserName;

    /// <summary>
    /// Rellena el formulario con lo elegido en el asistente, partiendo del disco (se descartan ediciones a
    /// medias de la página Configuración). <paramref name="password"/> null = conservar la guardada.
    /// </summary>
    public void ApplySetup(BleBondDevice device, string userName, string? password)
    {
        ReloadFromDisk();
        IrkText = device.Irk;
        IdentityAddressText = device.Address;
        IrkByteOrderIndex = 0;
        IrkHint = BuildIrkHint();
        UserNameText = userName.Trim();
        if (password is not null)
        {
            PasswordText = password;
        }
    }

    /// <summary>Descarta las ediciones y vuelve a la última copia leída o guardada (sin releer el disco).</summary>
    public void DiscardChanges()
    {
        ClearFieldErrors();
        ApplyToFields(_loadedConfig);
        BannerVisible = false;
        _ = LoadBondedDevicesAsync();
    }

    private void RecomputePending()
    {
        if (!CanEdit)
        {
            PendingCount = 0;
            PendingSummary = "";
            ModifiedFields = new HashSet<string>();
            return;
        }

        var (config, errors) = BuildFromForm();
        if (errors.Count > 0)
        {
            PendingCount = errors.Count;
            PendingSummary = UiStrings.Config_FieldFormatErrors;
            ModifiedFields = errors.Select(e => e.Field).ToHashSet(StringComparer.Ordinal);
            return;
        }

        var changes = ConfigDiff.Compute(_loadedConfig, config);
        PendingCount = changes.Count;
        PendingSummary = ConfigDiff.Summarize(changes);
        ModifiedFields = changes.Select(c => c.Field).ToHashSet(StringComparer.Ordinal);
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
                SetUnavailable(UiStrings.Config_NoDataDir);
                return;
            }

            if (!File.Exists(AppPaths.ConfigFile))
            {
                SetUnavailable(UiStrings.Config_NoConfigFile);
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
                ShowBanner(InfoBarSeverity.Warning, UiStrings.Config_InvalidTitle,
                    string.Format(UiStrings.Config_InvalidMessage, validation.Errors.Count));
            }

            // 跨机器拷贝等导致的不可解密密文：服务会拒绝启动，这里提前给出可操作提示（覆盖校验横幅）
            if (ConfigSecrets.IsUnreadableOnThisMachine(_loadedConfig.Device.Irk) ||
                ConfigSecrets.IsUnreadableOnThisMachine(_loadedConfig.Credentials.Password))
            {
                ShowBanner(InfoBarSeverity.Warning, UiStrings.Config_UndecryptableTitle,
                    UiStrings.Config_UndecryptableMessage);
            }
        }
        catch (Exception ex)
        {
            SetUnavailable(string.Format(UiStrings.Config_ReadFailed, DescribeException(ex)));
        }

        // 设备下拉框独立于配置文件加载：即使服务未运行，也会在状态行给出可读原因
        _ = LoadBondedDevicesAsync();
    }

    /// <summary>
    /// 重新读取 Windows 已配对 BLE 设备（服务以 SYSTEM 读取配对库）。
    /// 失败不抛异常：状态行显示原因，并保留「当前配置」合成项以免丢失已有 IRK。
    /// </summary>
    public async Task LoadBondedDevicesAsync()
    {
        if (_loadingBonds)
        {
            return;
        }

        _loadingBonds = true;
        BondStatusText = UiStrings.Bonds_Reading;
        try
        {
            var result = await _bondClient.ListAsync();
            var currentIrk = IrkText.Trim();
            var noDevice = string.Equals(currentIrk, ConfigValidator.NoDeviceIrk, StringComparison.OrdinalIgnoreCase);
            var items = new List<BleBondDevice>();

            if (result.Error is null)
            {
                // 没有 IRK 的设备也列出：按 MAC 地址追踪（只适用于地址固定的设备，显示时带标注）
                items.AddRange(result.Devices.Where(d => d.IsSupported || d.Address.Length > 0));
            }

            // 当前配置是按 MAC 地址追踪（没有 IRK、有地址）：选中同地址的无 IRK 条目；
            // 不在配对库里（扫描时选的未配对设备）就补一条合成项
            var macOnly = currentIrk.Length == 0 && ConfigValidator.NormalizeAddress(IdentityAddressText) is not null;
            BleBondDevice? macItem = null;
            if (macOnly)
            {
                var key = BondDiff.AddressKey(IdentityAddressText);
                macItem = items.FirstOrDefault(d => d.TracksByAddress &&
                    string.Equals(BondDiff.AddressKey(d.Address), key, StringComparison.OrdinalIgnoreCase));
                if (macItem is null)
                {
                    macItem = new BleBondDevice(BleAddress.Display(IdentityAddressText)!, null, "", Paired: false);
                    items.Add(macItem);
                }
            }

            // 当前配置的 IRK 不在配对库里（旧设备/跨机器/手工录入）：保留一条可选中的合成项
            // 未选择设备时使用的全零占位 IRK 不算「当前设备」，不显示合成项
            if (!noDevice &&
                currentIrk.Length > 0 &&
                !items.Any(d => string.Equals(d.Irk, currentIrk, StringComparison.OrdinalIgnoreCase)))
            {
                items.Add(new BleBondDevice("", UiStrings.Bonds_CurrentNotListed, currentIrk));
            }

            // Siempre al final: seguir un dispositivo por una MAC escrita a mano
            _manualItem = NewManualItem();
            items.Add(_manualItem);

            var selected = macOnly
                ? macItem
                : currentIrk.Length == 0 || noDevice
                    ? null
                    : items.FirstOrDefault(d =>
                        string.Equals(d.Irk, currentIrk, StringComparison.OrdinalIgnoreCase));

            _suppressBondSelection = true;
            try
            {
                BondedDevices.Clear();
                foreach (var item in items)
                {
                    BondedDevices.Add(item);
                }

                SelectedBondDevice = selected;
            }
            finally
            {
                _suppressBondSelection = false;
            }

            var pairedCount = items.Count(d => d.Paired && d.Address.Length > 0);
            BondStatusText = result.Error is not null
                ? string.Format(UiStrings.Bonds_ReadError, result.Error)
                : pairedCount == 0
                    ? UiStrings.Bonds_None
                    : noDevice
                        ? string.Format(UiStrings.Bonds_ReadNoneSelected, pairedCount)
                        : string.Format(UiStrings.Bonds_Read, pairedCount);

            IrkHint = BuildIrkHint();
        }
        catch (Exception ex)
        {
            BondStatusText = string.Format(UiStrings.Bonds_ReadFailed, ex.Message);
        }
        finally
        {
            _loadingBonds = false;
        }
    }

    /// <summary>用户从下拉框选择设备：IRK 与身份地址随之更新（IRK 不在界面明文显示）。</summary>
    partial void OnSelectedBondDeviceChanged(BleBondDevice? value)
    {
        if (_suppressBondSelection || value is null)
        {
            return;
        }

        // Sin IRK = seguimiento por MAC: el IRK se vacía y manda la dirección (la del dispositivo, o la que
        // escriba el usuario si eligió la entrada manual)
        IrkText = value.Irk;
        if (value.Address.Length > 0)
        {
            IdentityAddressText = value.Address;
        }
        else if (IsManualAddressSelected)
        {
            // La dirección del dispositivo anterior no vale: hay que escribir la nueva
            IdentityAddressText = "";
        }

        IrkByteOrderIndex = 0;
        IrkHint = BuildIrkHint();
    }

    // Entrada fija al final de la lista: seguir por una dirección MAC escrita a mano. Se crea en cada carga
    // para que el texto siga el idioma actual
    private BleBondDevice _manualItem = NewManualItem();

    private static BleBondDevice NewManualItem() => new("", UiStrings.Bonds_ManualAddress, "", Paired: false);

    private bool IsManualAddressSelected => ReferenceEquals(SelectedBondDevice, _manualItem);

    /// <summary>El formulario sigue el dispositivo solo por su MAC: se muestra el aviso de suplantación.</summary>
    public bool IsMacTracking =>
        IrkText.Trim().Length == 0 &&
        (IsManualAddressSelected || ConfigValidator.NormalizeAddress(IdentityAddressText) is not null);

    /// <summary>
    /// 配对向导返回后调用：重新读取配对库，按地址选中新设备并回填 IRK/地址。
    /// 保存由用户确认，避免向导直接改动服务配置。
    /// </summary>
    public async Task<bool> SelectBondedDeviceAsync(BleBondDevice picked)
    {
        // Sin emparejar (elegido en el escaneo para seguirlo por MAC): no está en el almacén de Windows
        if (!picked.Paired)
        {
            IrkText = "";
            IdentityAddressText = picked.Address;
            await LoadBondedDevicesAsync();
            return true;
        }

        var address = picked.Address;
        await LoadBondedDevicesAsync();
        var device = BondDiff.FindByAddress(BondedDevices, address);
        if (device is null)
        {
            ShowError(UiStrings.Bonds_NewNotFoundTitle,
                UiStrings.Bonds_NewNotFoundMessage);
            return false;
        }

        SelectedBondDevice = device;
        IrkText = device.Irk;
        if (device.Address.Length > 0)
        {
            IdentityAddressText = device.Address;
        }

        IrkByteOrderIndex = 0;
        IrkHint = BuildIrkHint();
        return true;
    }

    /// <summary>
    /// 解除当前选中设备的 Windows 配对。若它就是磁盘配置正在使用的设备：
    /// 写入全零占位 IRK（服务仍可启动/读取配对库，但没有任何设备可匹配，自动解锁暂停），
    /// 并确保服务在运行，以便随后打开配对向导。若只是未保存的下拉项，则只移除配对。
    /// </summary>
    public async Task<bool> UnpairSelectedDeviceAsync()
    {
        var device = SelectedBondDevice;
        if (device is null || device.Address.Length == 0)
        {
            return false;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        BlePairingResult unpair;
        try
        {
            unpair = await BlePairingService.UnpairAsync(device.Address, cts.Token);
        }
        catch (Exception ex)
        {
            ShowError(UiStrings.Unpair_FailedTitle, ex.Message);
            return false;
        }

        if (!unpair.Success)
        {
            ShowError(UiStrings.Unpair_FailedTitle, unpair.Message);
            return false;
        }

        var wasActive = IsLoadedDevice(device);
        if (!wasActive)
        {
            await LoadBondedDevicesAsync();
            var formStillUses = string.Equals(
                ConfigSecrets.DecryptForCompare(IrkText), device.Irk, StringComparison.OrdinalIgnoreCase);
            ShowBanner(
                formStillUses ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
                UiStrings.Unpair_DoneTitle,
                formStillUses
                    ? UiStrings.Unpair_DoneKeptIrk
                    : UiStrings.Unpair_DoneConfigUnaffected);
            return true;
        }

        // 当前设备：写入占位 IRK，保留一个服务可校验、但不会匹配任何真实设备的配置。
        BleUnlockConfig cleared;
        try
        {
            cleared = _loadedConfig.Clone();
            cleared.Device.Irk = ConfigValidator.NoDeviceIrk;
            cleared.Device.IdentityAddress = null;
            ConfigSecrets.ProtectNewValues(cleared);
            ConfigDocument.Save(AppPaths.ConfigFile, cleared);
        }
        catch (Exception ex)
        {
            // 配置没清掉就不能让旧 IRK 继续生效：先尝试停服务，再让用户重试。
            try
            {
                await _serviceControl.StopAsync();
            }
            catch
            {
                // 已在错误横幅中提示用户手动确认
            }

            ShowError(UiStrings.Unpair_ClearFailedTitle,
                string.Format(UiStrings.Unpair_ClearFailedMessage, ex.Message));
            await LoadBondedDevicesAsync();
            return false;
        }

        _loadedConfig = cleared;
        _ = _device.RefreshAsync();
        ClearFieldErrors();
        ApplyToFields(cleared);

        ServiceStatusInfo status;
        try
        {
            status = await _serviceControl.GetStatusAsync();
        }
        catch
        {
            status = new ServiceStatusInfo(ServiceStatusKind.Unknown);
        }

        var serviceReady = status.Kind == ServiceStatusKind.Running;
        if (!serviceReady && status.Kind != ServiceStatusKind.NotInstalled)
        {
            try
            {
                var start = await _serviceControl.StartAsync();
                serviceReady = start.Status == ServiceOperationStatus.Success;
            }
            catch
            {
                serviceReady = false;
            }
        }

        await LoadBondedDevicesAsync();

        if (serviceReady)
        {
            ShowBanner(InfoBarSeverity.Warning, UiStrings.Unpair_ClearedTitle,
                UiStrings.Unpair_ClearedMessage);
        }
        else
        {
            ShowBanner(InfoBarSeverity.Error, UiStrings.Unpair_ServiceStoppedTitle,
                UiStrings.Unpair_ServiceStoppedMessage);
        }

        return true;
    }

    /// <summary>判定一个配对库条目是否就是磁盘配置当前使用的设备（IRK 优先，地址兜底）。</summary>
    private bool IsLoadedDevice(BleBondDevice device)
    {
        var loadedIrk = ConfigSecrets.DecryptForCompare(_loadedConfig.Device.Irk);
        var loadedNormalized = ConfigValidator.NormalizeIrk(loadedIrk);
        var deviceNormalized = ConfigValidator.NormalizeIrk(device.Irk);
        if (loadedNormalized is not null && deviceNormalized is not null &&
            string.Equals(loadedNormalized, deviceNormalized, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var loadedAddress = ConfigValidator.NormalizeAddress(_loadedConfig.Device.IdentityAddress);
        var deviceAddress = ConfigValidator.NormalizeAddress(device.Address);
        return loadedAddress is not null && loadedAddress == deviceAddress;
    }

    /// <summary>IRK 字段提示（下拉选择语义；无法解密的密文单独提示）。</summary>
    private string BuildIrkHint(string? storedIrk = null)
    {
        var raw = storedIrk ?? IrkText;
        if (string.Equals(raw.Trim(), ConfigValidator.NoDeviceIrk, StringComparison.OrdinalIgnoreCase))
        {
            return UiStrings.Device_NoneSelectedPaused;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return ConfigValidator.NormalizeAddress(IdentityAddressText) is not null ? UiStrings.Device_TrackByAddress
                : IsManualAddressSelected ? UiStrings.Device_EnterAddress
                : UiStrings.Device_ChooseFromList;
        }

        if (ConfigSecrets.IsUnreadableOnThisMachine(raw))
        {
            return UiStrings.Device_IrkUndecryptable;
        }

        return UiStrings.Device_IrkAuto;
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
        if (IsManualAddressSelected && string.IsNullOrWhiteSpace(IdentityAddressText))
        {
            errors.Add(new ConfigError(ConfigFields.DeviceIdentityAddress, UiStrings.Validation_AddressRequiredForMac));
            brokenFields.Add(ConfigFields.DeviceIdentityAddress);
        }
        errors.AddRange(ConfigValidator.Validate(config).Errors.Where(e => !brokenFields.Contains(e.Field)));

        if (errors.Count > 0)
        {
            ApplyFieldErrors(errors);
            ShowBanner(InfoBarSeverity.Error, UiStrings.Save_InvalidTitle,
                string.Format(UiStrings.Save_InvalidMessage, errors.Count));
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
            ShowBanner(InfoBarSeverity.Informational, UiStrings.Save_NothingTitle, UiStrings.Save_NothingMessage);
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
            var checkpoint = ConfigReloadWatcher.CaptureCheckpoint(AppPaths.ReloadStatusFile);
            ConfigDocument.Save(AppPaths.ConfigFile, config);

            // 写盘成功后：文件真值更新（表单回填归一化结果）
            _loadedConfig = config;
            ApplyToFields(config);
            ClearFieldErrors();
            RecomputePending();
            _ = _device.RefreshAsync();

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
                    ? UiStrings.Save_ServiceStopped
                    : UiStrings.Save_ServiceUnknown;
                ShowBanner(InfoBarSeverity.Success, UiStrings.Save_SavedTitle, message,
                    ConfigBannerAction.StartService, UiStrings.Common_StartService);
                return;
            }

            ShowBanner(InfoBarSeverity.Informational, UiStrings.Save_WrittenTitle,
                UiStrings.Save_WaitingReload);

            var outcome = await ConfigReloadWatcher.WaitAsync(AppPaths.ReloadStatusFile, checkpoint, ReloadWaitTimeout);
            switch (outcome.Status)
            {
                case ConfigReloadStatus.Reloaded:
                    ShowBanner(InfoBarSeverity.Success, UiStrings.Save_ReloadedTitle, BuildReloadedMessage(outcome));
                    break;

                case ConfigReloadStatus.Rejected:
                    ShowBanner(InfoBarSeverity.Error, UiStrings.Save_RejectedTitle,
                        outcome.Message ?? UiStrings.Save_RejectedFallback);
                    break;

                default:
                    ShowBanner(InfoBarSeverity.Warning, UiStrings.Save_UnconfirmedTitle,
                        UiStrings.Save_UnconfirmedMessage,
                        ConfigBannerAction.RestartService, UiStrings.Common_RestartService);
                    break;
            }
        }
        catch (Exception ex)
        {
            ShowBanner(InfoBarSeverity.Error, UiStrings.Save_FailedTitle, DescribeException(ex));
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

        var succeededTitle = action == ConfigBannerAction.StartService ? UiStrings.Op_ServiceStartedTitle : UiStrings.Op_ServiceRestartedTitle;
        var succeededMessage = action == ConfigBannerAction.StartService
            ? UiStrings.Op_StartedWithLatest
            : UiStrings.Op_RestartedWithLatest;

        switch (result.Status)
        {
            case ServiceOperationStatus.Success:
                ShowBanner(InfoBarSeverity.Success, succeededTitle, succeededMessage);
                break;
            case ServiceOperationStatus.NotInstalled:
                ShowBanner(InfoBarSeverity.Error, UiStrings.Op_NotInstalledTitle, result.Message ?? UiStrings.Op_NotInstalledFallback);
                break;
            case ServiceOperationStatus.AccessDenied:
                // 保留同一动作按钮：权限/瞬态问题修正后可直接重试（评审 M6）
                ShowBanner(InfoBarSeverity.Error, UiStrings.Op_AccessDeniedTitle, result.Message ?? UiStrings.Op_AdminRequired,
                    action, ActionText(action));
                break;
            case ServiceOperationStatus.Timeout:
                ShowBanner(InfoBarSeverity.Warning, UiStrings.Op_TimeoutTitle, result.Message ?? UiStrings.Op_TimeoutFallback,
                    action, ActionText(action));
                break;
            default:
                ShowBanner(InfoBarSeverity.Error, UiStrings.Common_OperationFailed, result.Message ?? UiStrings.Op_UnknownError,
                    action, ActionText(action));
                break;
        }
    }

    /// <summary>动作按钮文案（横幅动作与失败重试路径共用）。</summary>
    private static string ActionText(ConfigBannerAction action)
        => action == ConfigBannerAction.StartService ? UiStrings.Common_StartService : UiStrings.Common_RestartService;

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

    private (BleUnlockConfig Config, List<ConfigError> Errors) BuildFromForm()
    {
        var config = new BleUnlockConfig();
        var errors = new List<ConfigError>();

        // 敏感字段：表单显示当前值（磁盘密文已在 ApplyToFields 解密回显）；
        // 直接用表单文本构建，保存时由 ProtectNewValues 重新加密
        config.Device.Irk = IrkText.Trim();
        config.Device.IrkByteOrder = (IrkByteOrder)Math.Clamp(IrkByteOrderIndex, 0, 2);
        config.Device.IdentityAddress = string.IsNullOrWhiteSpace(IdentityAddressText)
            ? null
            : IdentityAddressText.Trim();
        config.Credentials.UserName = UserNameText;
        // Campo vacío = se conserva la guardada
        config.Credentials.Password = _password.Effective;

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
        if (TryGetCount(SampleIntervalText, ConfigFields.ProximitySampleIntervalMs, errors, out var sampleInterval))
            p.SampleIntervalMs = sampleInterval;
        if (TryGetNumber(StartupGraceText, ConfigFields.ProximityStartupGraceSeconds, errors, out var startupGrace))
            p.StartupGraceSeconds = startupGrace;
        if (TryGetNumber(UnlockPendingTimeoutText, ConfigFields.ProximityUnlockPendingTimeoutSeconds, errors, out var unlockPending))
            p.UnlockPendingTimeoutSeconds = unlockPending;
        if (TryGetNumber(NoSignalDepartureText, ConfigFields.ProximityNoSignalDepartureSeconds, errors, out var noSignal))
            p.NoSignalDepartureSeconds = noSignal;

        var n = config.Notification;
        n.Mode = (NotifierMode)Math.Clamp(NotifierModeIndex, 0, 1);
        if (TryGetNumber(RejectSnoozeText, ConfigFields.NotificationRejectSnoozeSeconds, errors, out var rejectSnooze))
            n.RejectSnoozeSeconds = rejectSnooze;
        if (TryGetNumber(SnoozeText, ConfigFields.NotificationSnoozeMinutes, errors, out var snooze))
            n.SnoozeMinutes = snooze;

        return (config, errors);
    }

    private void ApplyToFields(BleUnlockConfig config)
    {
        IrkText = ConfigSecrets.DisplayText(config.Device.Irk);
        IrkByteOrderIndex = Math.Clamp((int)config.Device.IrkByteOrder, 0, 2);
        IdentityAddressText = config.Device.IdentityAddress ?? "";
        UserNameText = config.Credentials.UserName;
        // La guardada se queda en el modelo; el campo vuelve a vacío
        _password.Load(ConfigSecrets.DisplayText(config.Credentials.Password));
        PasswordText = "";
        OnPropertyChanged(nameof(CanRevealPassword));
        OnPropertyChanged(nameof(PasswordPlaceholder));
        LockThresholdText = ConfigInput.FormatNumber(config.Proximity.LockThresholdDbm);
        UnlockThresholdText = ConfigInput.FormatNumber(config.Proximity.UnlockThresholdDbm);
        UnlockCountText = ConfigInput.FormatCount(config.Proximity.UnlockSampleCount);
        LockPromptCountText = ConfigInput.FormatCount(config.Proximity.LockPromptSampleCount);
        LockConfirmCountText = ConfigInput.FormatCount(config.Proximity.LockConfirmSampleCount);
        SampleIntervalText = ConfigInput.FormatCount(config.Proximity.SampleIntervalMs);
        StartupGraceText = ConfigInput.FormatNumber(config.Proximity.StartupGraceSeconds);
        UnlockPendingTimeoutText = ConfigInput.FormatNumber(config.Proximity.UnlockPendingTimeoutSeconds);
        NoSignalDepartureText = ConfigInput.FormatNumber(config.Proximity.NoSignalDepartureSeconds);
        NotifierModeIndex = Math.Clamp((int)config.Notification.Mode, 0, 1);
        RejectSnoozeText = ConfigInput.FormatNumber(config.Notification.RejectSnoozeSeconds);
        SnoozeText = ConfigInput.FormatNumber(config.Notification.SnoozeMinutes);
        IrkHint = BuildIrkHint(config.Device.Irk);
        PasswordHint = ConfigSecrets.HintText(config.Credentials.Password);
    }

    private static bool TryGetNumber(string text, string field, List<ConfigError> errors, out double value)
    {
        if (ConfigInput.TryParseNumber(text, out value))
        {
            return true;
        }

        errors.Add(new ConfigError(field, string.Format(UiStrings.Input_MustBeNumber, ConfigDiff.GetDisplayName(field))));
        return false;
    }

    private static bool TryGetCount(string text, string field, List<ConfigError> errors, out int value)
    {
        if (ConfigInput.TryParseCount(text, out value))
        {
            return true;
        }

        errors.Add(new ConfigError(field, string.Format(UiStrings.Input_MustBeInteger, ConfigDiff.GetDisplayName(field))));
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
            case ConfigFields.ProximitySampleIntervalMs:
                if (SampleIntervalError.Length == 0) SampleIntervalError = error.Message;
                break;
            case ConfigFields.ProximityStartupGraceSeconds:
                if (StartupGraceError.Length == 0) StartupGraceError = error.Message;
                break;
            case ConfigFields.ProximityUnlockPendingTimeoutSeconds:
                if (UnlockPendingTimeoutError.Length == 0) UnlockPendingTimeoutError = error.Message;
                break;
            case ConfigFields.ProximityNoSignalDepartureSeconds:
                if (NoSignalDepartureError.Length == 0) NoSignalDepartureError = error.Message;
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
        SampleIntervalError = "";
        StartupGraceError = "";
        UnlockPendingTimeoutError = "";
        NoSignalDepartureError = "";
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
                OnPropertyChanged(nameof(IsMacTracking));
                break;
            case nameof(IdentityAddressText):
                if (IdentityAddressError is { Length: > 0 }) IdentityAddressError = "";
                OnPropertyChanged(nameof(IsMacTracking));
                break;
            case nameof(SelectedBondDevice):
                OnPropertyChanged(nameof(IsMacTracking));
                break;
            case nameof(UserNameText):
                if (UserNameError is { Length: > 0 }) UserNameError = "";
                break;
            case nameof(PasswordText):
                _password.Edit(PasswordText);
                OnPropertyChanged(nameof(CanRevealPassword));
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
            case nameof(SampleIntervalText):
                if (SampleIntervalError is { Length: > 0 }) SampleIntervalError = "";
                break;
            case nameof(StartupGraceText):
                if (StartupGraceError is { Length: > 0 }) StartupGraceError = "";
                break;
            case nameof(UnlockPendingTimeoutText):
                if (UnlockPendingTimeoutError is { Length: > 0 }) UnlockPendingTimeoutError = "";
                break;
            case nameof(NoSignalDepartureText):
                if (NoSignalDepartureError is { Length: > 0 }) NoSignalDepartureError = "";
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

        if (e.PropertyName is nameof(LockThresholdText))
        {
            OnPropertyChanged(nameof(LockThresholdPreview));
        }
        else if (e.PropertyName is nameof(UnlockThresholdText))
        {
            OnPropertyChanged(nameof(UnlockThresholdPreview));
        }

        if (e.PropertyName is { } name && (FormProperties.Contains(name) || name == nameof(CanEdit)))
        {
            RecomputePending();
        }
    }

    private void SetUnavailable(string warning)
    {
        CanEdit = false;
        CanOpenFolder = Directory.Exists(AppPaths.DataDir);
        PathWarningVisible = true;
        PathWarningText = string.Format(UiStrings.Config_PathWarning, warning, AppPaths.ConfigFile);
    }

    private static string BuildReloadedMessage(ConfigReloadOutcome outcome)
    {
        if (outcome.DeviceChanged is null && outcome.CredentialsChanged is null)
        {
            return UiStrings.Reload_Applied;
        }

        var device = outcome.DeviceChanged == true ? UiStrings.Common_Yes : UiStrings.Common_No;
        var credentials = outcome.CredentialsChanged == true ? UiStrings.Common_Yes : UiStrings.Common_No;
        return string.Format(UiStrings.Reload_AppliedWithFlags, device, credentials);
    }

    private static string DescribeException(Exception ex) => ex switch
    {
        System.Text.Json.JsonException => string.Format(UiStrings.Error_Json, ex.Message),
        UnauthorizedAccessException => string.Format(UiStrings.Error_AccessDenied, ex.Message),
        // SecretProtector se comparte con el servicio y lanza sin texto propio
        System.Security.Cryptography.CryptographicException => UiStrings.Error_Encryption,
        IOException => ex.Message,
        _ => ex.Message,
    };
}

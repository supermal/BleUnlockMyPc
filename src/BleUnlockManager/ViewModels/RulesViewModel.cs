using System.Collections.ObjectModel;
using System.Windows.Threading;
using BleUnlockManager.Core;
using BleUnlockManager.Core.Config;
using BleUnlockManager.Core.Localization;
using BleUnlockManager.Core.Resources;
using BleUnlockManager.Core.Rules;
using BleUnlockManager.Core.Telemetry;
using BleUnlockManager.Services;

namespace BleUnlockManager.ViewModels;

/// <summary>Estado del diagrama: posición fija, nombre, y si es el actual (con el progreso del lote).</summary>
public sealed partial class FlowNode(string state, double x, double y, int section, bool isSide, bool isWarn) : ObservableObject
{
    public string State { get; } = state;
    public double X { get; } = x;
    public double Y { get; } = y;

    /// <summary>Sección a la que salta al pulsarlo.</summary>
    public int Section { get; } = section;

    public bool IsSide { get; } = isSide;
    public bool IsWarn { get; } = isWarn;

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Sub { get; set; } = "";
    [ObservableProperty] public partial bool IsCurrent { get; set; }
    [ObservableProperty] public partial bool HasProgress { get; set; }

    /// <summary>0–1.</summary>
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string ProgressText { get; set; } = "";
}

public sealed record RuleStepView(int Number, string Text);

public sealed record RuleGroupView(string Title, IReadOnlyList<RuleStepView> Steps);

/// <summary>Sección lista para la vista: qué bloques tiene y los pasos ya numerados.</summary>
public sealed record RuleSectionView(RuleSection Section, IReadOnlyList<RuleGroupView> Groups)
{
    public bool HasLead => Section.Lead is not null;
    public bool HasParams => Section.Params.Count > 0;
    public bool HasItems => Section.Items.Count > 0;
    public bool HasRows => Section.Rows.Count > 0;
    public bool HasGroups => Groups.Count > 0;
}

/// <summary>
/// Página de reglas: el texto se genera con los valores que usa el servicio (appsettings.json) y el
/// diagrama marca el estado actual con la telemetría. Mientras la página se ve, cada 2 s se mira si
/// el servicio recargó la configuración (reload-status.json) para volver a leerla.
/// </summary>
public sealed partial class RulesViewModel : ObservableObject
{
    private static readonly TimeSpan ReloadCheckInterval = TimeSpan.FromSeconds(2);

    private readonly DispatcherTimer _reloadTimer;
    private RuleValues _values = RuleValues.Defaults;
    private DateTime _readAt;
    private string? _statusId;
    private bool _active;

    public RulesViewModel(TelemetryStore telemetry)
    {
        Telemetry = telemetry;
        Nodes =
        [
            new FlowNode("Unlocked", 0, 150, 3, false, false),
            new FlowNode("PendingLock", 200, 150, 4, false, false),
            new FlowNode("LockedWaitingDeparture", 400, 150, 5, false, false),
            new FlowNode("LockedArmed", 600, 150, 6, false, false),
            new FlowNode("UnlockPending", 800, 150, 6, false, false),
            new FlowNode("Snoozed", 205, 290, 4, true, false),
            new FlowNode("Suspended", 606, 290, 7, true, true),
        ];

        _reloadTimer = new DispatcherTimer { Interval = ReloadCheckInterval };
        _reloadTimer.Tick += (_, _) => ReloadIfServiceReloaded();
        telemetry.Updated += (_, _) =>
        {
            if (_active)
            {
                UpdateLive();
            }
        };
        ManagerLanguage.Changed += () =>
        {
            if (_active)
            {
                Rebuild();
            }
        };
    }

    public TelemetryStore Telemetry { get; }

    public ObservableCollection<FlowNode> Nodes { get; }

    [ObservableProperty] public partial IReadOnlyList<RuleSectionView> Sections { get; set; } = [];
    [ObservableProperty] public partial bool UsingDefaults { get; set; }
    [ObservableProperty] public partial string SourceText { get; set; } = "";
    [ObservableProperty] public partial string SourceTone { get; set; } = "neutral";
    [ObservableProperty] public partial string LiveText { get; set; } = "";
    [ObservableProperty] public partial string LiveTone { get; set; } = "neutral";

    // Rótulos de las flechas
    [ObservableProperty] public partial string EdgeWithdraw { get; set; } = "";
    [ObservableProperty] public partial string EdgeSuccess { get; set; } = "";
    [ObservableProperty] public partial string EdgeFail { get; set; } = "";
    [ObservableProperty] public partial string EdgeLeave { get; set; } = "";
    [ObservableProperty] public partial string EdgeConfirm { get; set; } = "";
    [ObservableProperty] public partial string EdgeDepart { get; set; } = "";
    [ObservableProperty] public partial string EdgeNear { get; set; } = "";
    [ObservableProperty] public partial string EdgeSnoozeIn { get; set; } = "";
    [ObservableProperty] public partial string EdgeSnoozeOut { get; set; } = "";

    /// <summary>La página lo llama al mostrarse u ocultarse: oculta no lee ni recalcula nada.</summary>
    public void SetActive(bool active)
    {
        if (_active == active)
        {
            return;
        }

        _active = active;
        if (active)
        {
            Load();
            _reloadTimer.Start();
        }
        else
        {
            _reloadTimer.Stop();
        }
    }

    private void Load()
    {
        _statusId = ConfigReloadWatcher.CaptureCheckpoint(AppPaths.ReloadStatusFile).Id;
        _values = RuleValues.Load(AppPaths.ConfigFile, out var fromConfig);
        UsingDefaults = !fromConfig;
        _readAt = DateTime.Now;
        Rebuild();
    }

    private void ReloadIfServiceReloaded()
    {
        var id = ConfigReloadWatcher.CaptureCheckpoint(AppPaths.ReloadStatusFile).Id;
        if (id != _statusId)
        {
            Load();
        }
    }

    private void Rebuild()
    {
        Sections = RulesContent.Build(_values)
            .Select(s => new RuleSectionView(s, s.StepGroups
                .Select(g => new RuleGroupView(g.Title, g.Steps.Select((t, i) => new RuleStepView(i + 1, t)).ToList()))
                .ToList()))
            .ToList();

        SourceText = UsingDefaults
            ? UiStrings.Rules_SrcDefault
            : UiStrings.Rules_SrcLive.Replace("{time}", _readAt.ToString("HH:mm"));
        SourceTone = UsingDefaults ? "neutral" : "ok";

        EdgeWithdraw = RulesContent.Fill("Rules_Edge_Withdraw", _values);
        EdgeSuccess = RulesContent.Fill("Rules_Edge_Success", _values);
        EdgeFail = RulesContent.Fill("Rules_Edge_Fail", _values);
        EdgeLeave = RulesContent.Fill("Rules_Edge_Leave", _values);
        EdgeConfirm = RulesContent.Fill("Rules_Edge_Confirm", _values);
        EdgeDepart = RulesContent.Fill("Rules_Edge_Depart", _values);
        EdgeNear = RulesContent.Fill("Rules_Edge_Near", _values);
        EdgeSnoozeIn = RulesContent.Fill("Rules_Edge_SnoozeIn", _values);
        EdgeSnoozeOut = RulesContent.Fill("Rules_Edge_SnoozeOut", _values);

        foreach (var node in Nodes)
        {
            node.Name = TelemetryFormat.StateName(node.State);
            node.Sub = UiStrings.ResourceManager.GetString($"Rules_Sub_{node.State}", UiStrings.Culture) ?? "";
        }

        UpdateLive();
    }

    private void UpdateLive()
    {
        var state = Telemetry.Connected ? Telemetry.StateName : null;
        var batch = Telemetry.Batch;
        foreach (var node in Nodes)
        {
            var current = node.State == state;
            node.IsCurrent = current;
            node.HasProgress = current && batch is { Of: > 0 };
            node.Progress = node.HasProgress ? Math.Clamp((double)batch!.N / batch.Of, 0, 1) : 0;
            node.ProgressText = node.HasProgress ? $"{BatchLabel(batch!.Kind)} {BatchText.Progress(batch)}" : "";
        }

        if (state is null)
        {
            LiveText = UiStrings.Rules_LiveOff;
            LiveTone = "neutral";
            return;
        }

        var text = UiStrings.Rules_LiveOn.Replace("{state}", TelemetryFormat.StateName(state));
        LiveText = batch is { Of: > 0 } ? $"{text} · {BatchLabel(batch.Kind)} {BatchText.Progress(batch)}" : text;
        LiveTone = "info";
    }

    private static string BatchLabel(string kind) => kind switch
    {
        "lockConfirm" => UiStrings.Batch_LockConfirm,
        "unlock" => UiStrings.Batch_Unlock,
        "suddenDrop" => UiStrings.Batch_SuddenDrop,
        "suddenRise" => UiStrings.Batch_SuddenRise,
        _ => UiStrings.Batch_Leave,
    };
}

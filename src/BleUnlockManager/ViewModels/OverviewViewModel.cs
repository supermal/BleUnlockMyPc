using System.Text;
using BleUnlockManager.Core;
using BleUnlockManager.Core.Management;
using BleUnlockManager.Core.Telemetry;
using BleUnlockManager.Services;
using Wpf.Ui.Controls;
using BleUnlockManager.Core.Resources;

namespace BleUnlockManager.ViewModels;

/// <summary>Operación de control del servicio (botones de la vista general, previa confirmación).</summary>
public enum ServiceOperationKind
{
    Start,
    Stop,
    Restart,
}

/// <summary>Fila de la lista «服务与健康». Tone: ok | warn | danger | neutral | plain (texto sin distintivo).</summary>
public sealed record HealthRow(string Key, string Value, string Sub, string Tone);

/// <summary>
/// Vista general: señal en directo, ciclo de bloqueo, salud del servicio y dispositivo objetivo.
/// Los datos vienen de <see cref="TelemetryStore"/> (no abre un cliente propio); este modelo solo
/// los adapta a la página y ejecuta las operaciones del servicio.
/// </summary>
public sealed partial class OverviewViewModel : ObservableObject
{
    private readonly ServiceControlService _serviceControl;

    // Inactiva hasta que la página se vea: la aplicación puede arrancar directamente en la bandeja
    private bool _viewActive;

    public OverviewViewModel(TelemetryStore telemetry, ServiceControlService serviceControl)
    {
        Telemetry = telemetry;
        _serviceControl = serviceControl;
        HealthRows = [];
        ChartPoints = [];
        BannerTitle = "";
        BannerMessage = "";
        Refresh();
        telemetry.Updated += (_, _) => Refresh();
    }

    public TelemetryStore Telemetry { get; }

    // —— Curva ——
    [ObservableProperty] public partial IReadOnlyList<SamplePoint> ChartPoints { get; set; }
    [ObservableProperty] public partial long ChartNowMs { get; set; }
    [ObservableProperty] public partial double LockDbm { get; set; } = double.NaN;
    [ObservableProperty] public partial double UnlockDbm { get; set; } = double.NaN;

    // —— Tarjeta de señal ——
    public string Avg5Text => TelemetryFormat.Dbm(Telemetry.Snapshot?.Avg?.W5);

    public string Avg10Text => TelemetryFormat.Dbm(Telemetry.Snapshot?.Avg?.W10);

    public string ZoneTone => Telemetry.Zone switch
    {
        SignalZone.Near => "ok",
        SignalZone.Away => "danger",
        _ => "neutral",
    };

    private bool IsLeaving => Telemetry.IsLeaving;

    public string StateTone => IsLeaving ? "warn" : "info";

    public string StateSubText => IsLeaving
        ? UiStrings.Overview_ConfirmingDeparture
        : Telemetry.Connected ? string.Format(UiStrings.Overview_StateSince, Telemetry.StateSinceText) : "";

    [ObservableProperty] public partial IReadOnlyList<HealthRow> HealthRows { get; set; }

    // —— Control del servicio ——
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(CanStopOrRestart))]
    public partial bool OperationInProgress { get; set; }

    public bool CanStart =>
        !OperationInProgress
        && Telemetry.ServiceStatus is ServiceStatusKind.Stopped;

    public bool CanStopOrRestart => !OperationInProgress && Telemetry.ServiceStatus is ServiceStatusKind.Running;

    // —— Avisos ——
    public bool ConnectionBannerVisible => !Telemetry.Connected;

    [ObservableProperty] public partial bool BannerOpen { get; set; }
    [ObservableProperty] public partial InfoBarSeverity BannerSeverity { get; set; }
    [ObservableProperty] public partial string BannerTitle { get; set; }
    [ObservableProperty] public partial string BannerMessage { get; set; }

    public void ShowBanner(InfoBarSeverity severity, string title, string message)
    {
        BannerSeverity = severity;
        BannerTitle = title;
        BannerMessage = message;
        BannerOpen = true;
    }

    /// <summary>Exporta las muestras del búfer (5 minutos) a CSV en UTF-8 sin BOM.</summary>
    public async Task ExportCsvAsync(string path)
    {
        try
        {
            var samples = Telemetry.History.Snapshot();
            await File.WriteAllTextAsync(path, SampleCsv.Build(samples), new UTF8Encoding(false));
            ShowBanner(InfoBarSeverity.Success, UiStrings.Overview_Exported, string.Format(UiStrings.Overview_ExportedMessage, samples.Count, path));
        }
        catch (Exception ex)
        {
            ShowBanner(InfoBarSeverity.Error, UiStrings.Overview_ExportFailed, ex.Message);
        }
    }

    public async Task RunServiceOperationAsync(ServiceOperationKind kind)
    {
        if (OperationInProgress)
        {
            return;
        }

        OperationInProgress = true;
        ServiceOperationResult result;
        try
        {
            result = kind switch
            {
                ServiceOperationKind.Start => await _serviceControl.StartAsync(),
                ServiceOperationKind.Stop => await _serviceControl.StopAsync(),
                _ => await _serviceControl.RestartAsync(),
            };
        }
        catch (Exception ex)
        {
            result = new ServiceOperationResult(ServiceOperationStatus.Failure, ex.Message);
        }

        OperationInProgress = false;
        // Frases completas por operación: el orden de las palabras cambia de un idioma a otro
        var (doneTitle, doneMessage, failedTitle) = kind switch
        {
            ServiceOperationKind.Start => (UiStrings.Op_StartedTitle, UiStrings.Op_StartedMessage, UiStrings.Op_StartFailed),
            ServiceOperationKind.Stop => (UiStrings.Op_StoppedTitle, UiStrings.Op_StoppedMessage, UiStrings.Op_StopFailed),
            _ => (UiStrings.Op_RestartedTitle, UiStrings.Op_RestartedMessage, UiStrings.Op_RestartFailed),
        };

        if (result.Status == ServiceOperationStatus.Success)
        {
            ShowBanner(InfoBarSeverity.Success, doneTitle, doneMessage);
        }
        else
        {
            ShowBanner(InfoBarSeverity.Error, failedTitle, DescribeFailure(result));
        }

        await Telemetry.RefreshServiceStatusAsync();
    }

    /// <summary>
    /// La página lo llama al mostrarse u ocultarse (otra página, ventana oculta en la bandeja o
    /// minimizada). Oculta no se recalcula nada: al volver se pone al día de una vez.
    /// </summary>
    public void SetViewActive(bool active)
    {
        if (_viewActive == active)
        {
            return;
        }

        _viewActive = active;
        if (active)
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        if (!_viewActive)
        {
            return;
        }

        var snapshot = Telemetry.Snapshot;
        ChartPoints = Telemetry.History.Downsample();
        ChartNowMs = snapshot?.TsMs ?? ChartNowMs;
        LockDbm = snapshot?.Thr?.LockDbm ?? LockDbm;
        UnlockDbm = snapshot?.Thr?.UnlockDbm ?? UnlockDbm;

        var rows = BuildHealthRows(snapshot);
        if (!rows.SequenceEqual(HealthRows))
        {
            HealthRows = rows;
        }

        OnPropertyChanged(nameof(Avg5Text));
        OnPropertyChanged(nameof(Avg10Text));
        OnPropertyChanged(nameof(ZoneTone));
        OnPropertyChanged(nameof(StateTone));
        OnPropertyChanged(nameof(StateSubText));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStopOrRestart));
        OnPropertyChanged(nameof(ConnectionBannerVisible));
    }

    private IReadOnlyList<HealthRow> BuildHealthRows(SnapshotFrame? snapshot)
    {
        var service = Telemetry.ServiceStatus switch
        {
            ServiceStatusKind.Running => new HealthRow(UiStrings.Health_Service, UiStrings.Health_Running,
                snapshot is null ? "" : string.Format(UiStrings.Health_Uptime, TelemetryFormat.Uptime(snapshot.UptimeSec)), "ok"),
            ServiceStatusKind.Stopped => new HealthRow(UiStrings.Health_Service, UiStrings.Health_Stopped, UiStrings.Health_StoppedDetail, "danger"),
            ServiceStatusKind.NotInstalled => new HealthRow(UiStrings.Health_Service, UiStrings.Health_NotInstalled, UiStrings.Health_RunInstaller, "danger"),
            ServiceStatusKind.StartPending => new HealthRow(UiStrings.Health_Service, UiStrings.Health_Starting, "", "neutral"),
            ServiceStatusKind.StopPending => new HealthRow(UiStrings.Health_Service, UiStrings.Health_Stopping, "", "neutral"),
            _ => new HealthRow(UiStrings.Health_Service, UiStrings.Health_Querying, "", "neutral"),
        };

        if (snapshot is null)
        {
            return
            [
                service,
                new HealthRow(UiStrings.Health_BleScan, "—", "", "plain"),
                new HealthRow(UiStrings.Health_Helper, "—", "", "plain"),
                new HealthRow(UiStrings.Health_CredProvider, "—", "", "plain"),
                new HealthRow(UiStrings.Health_LastMatch, "—", "", "plain"),
            ];
        }

        var scan = TelemetryFormat.ClassifyScan(snapshot.Scan?.Code) switch
        {
            ScanHealth.Running => new HealthRow(UiStrings.Health_BleScan, UiStrings.Health_Normal, "", "ok"),
            ScanHealth.Degraded => new HealthRow(UiStrings.Health_BleScan, UiStrings.Health_Abnormal, ScanProblem(snapshot.Scan), "warn"),
            _ => new HealthRow(UiStrings.Health_BleScan, "—", "", "plain"),
        };

        var helper = snapshot.Helper is { Connected: true }
            ? new HealthRow(UiStrings.Health_Helper, UiStrings.Health_Connected, "", "ok")
            : new HealthRow(UiStrings.Health_Helper, UiStrings.Health_Disconnected, UiStrings.Health_HelperMissing, "warn");

        // El proveedor de credenciales solo vive en la pantalla de bloqueo: desconectado es lo normal
        var cred = snapshot.Cred is { Connected: true }
            ? new HealthRow(UiStrings.Health_CredProvider, UiStrings.Health_Connected, "", "ok")
            : new HealthRow(UiStrings.Health_CredProvider, UiStrings.Health_Disconnected, UiStrings.Health_CredOnlyWhenLocked, "neutral");

        var stale = TelemetryFormat.IsLastMatchStale(snapshot.LastMatchMs, snapshot.UptimeSec);
        var lastMatch = new HealthRow(UiStrings.Health_LastMatch, TelemetryFormat.LastMatch(snapshot.LastMatchMs),
            stale ? UiStrings.Health_Stale : "", stale ? "warn" : "plain");

        return [service, scan, helper, cred, lastMatch];
    }

    // El texto sale del código del servicio (no de su frase, que va en el idioma del servicio);
    // el detalle es el mensaje original del error, si lo hubo
    private static string ScanProblem(SnapshotScan? scan)
    {
        var text = UiStrings.ResourceManager.GetString($"Scan_{scan?.Code}", UiStrings.Culture) ?? UiStrings.Scan_unknown;
        return string.IsNullOrWhiteSpace(scan?.Detail) ? text : $"{text} ({scan.Detail})";
    }

    private static string DescribeFailure(ServiceOperationResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            return result.Message!;
        }

        return result.Status switch
        {
            ServiceOperationStatus.NotInstalled => UiStrings.Op_NotInstalled,
            ServiceOperationStatus.AccessDenied => UiStrings.Op_AccessDenied,
            ServiceOperationStatus.Timeout => UiStrings.Op_Timeout,
            _ => UiStrings.Op_UnknownError,
        };
    }
}

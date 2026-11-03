using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Controls;
using BleUnlockManager.Core;
using BleUnlockManager.Core.Config;
using BleUnlockManager.Core.Localization;
using BleUnlockManager.Core.Logs;
using BleUnlockManager.Models;
using BleUnlockManager.Services;
using BleUnlockManager.ViewModels;
using BleUnlockManager.Views;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui;
using Wpf.Ui.Controls;
using BleUnlockManager.Core.Resources;

namespace BleUnlockManager;

public partial class MainWindow : FluentWindow
{
    private readonly IServiceProvider _services;
    private bool _exiting;
    private bool _syncingSelection;

    public MainWindow(IServiceProvider services, IContentDialogService dialogs, TelemetryStore telemetry, LogsViewModel logs)
    {
        _services = services;
        Telemetry = telemetry;

        MenuItems =
        [
            new NavItem(AppPage.Overview, () => UiStrings.Nav_Overview, SymbolRegular.Pulse24),
            new NavItem(AppPage.Config, () => UiStrings.Nav_Config, SymbolRegular.Options24),
            new NavItem(AppPage.Logs, () => UiStrings.Nav_Logs, SymbolRegular.DocumentText24),
            new NavItem(AppPage.Rules, () => UiStrings.Rules_Nav, SymbolRegular.Flowchart24),
        ];
        FooterItems = [new NavItem(AppPage.Settings, () => UiStrings.Nav_Settings, SymbolRegular.Settings24)];

        InitializeComponent();
        dialogs.SetDialogHost(RootContentDialog);

        // Contador junto a «日志»: errores y advertencias escritos desde la última vez que se abrió
        // la página (errores en rojo por encima de advertencias); al abrirla desaparece
        _logs = logs;
        _logsItem = MenuItems.First(i => i.Page == AppPage.Logs);
        _logsSeenUntil = LogSeenStore.Load(AppPaths.LogsSeenFile);
        logs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LogsViewModel.Counts))
            {
                RefreshLogsBadge();
            }
        };
        ManagerLanguage.Changed += () => Dispatcher.BeginInvoke(RefreshLogsBadge);

        // Oculta en la bandeja o minimizada no se sigue el registro; al volver se pone al día
        void UpdateLogsActive()
        {
            logs.SetWindowActive(IsVisible && WindowState != WindowState.Minimized);
            if (CurrentPage == AppPage.Logs)
            {
                // Volver a la ventana con «日志» abierto cuenta como verlo; ocultarla guarda la marca
                MarkLogsSeen(persist: !LogsVisibleToUser);
                RefreshLogsBadge();
            }
        }

        IsVisibleChanged += (_, _) => UpdateLogsActive();
        StateChanged += (_, _) => UpdateLogsActive();

        Navigate(AppPage.Overview);
    }

    /// <summary>Telemetría compartida (barra de título y vista rápida del dispositivo).</summary>
    public TelemetryStore Telemetry { get; }

    // —— Contador «sin leer» de la navegación ——

    private readonly LogsViewModel _logs;
    private readonly NavItem _logsItem;
    private DateTimeOffset? _logsSeenUntil;

    // Solo cuenta como leído lo que de verdad está en pantalla
    private bool LogsVisibleToUser =>
        CurrentPage == AppPage.Logs && IsVisible && WindowState != WindowState.Minimized;

    private void RefreshLogsBadge()
    {
        if (LogsVisibleToUser)
        {
            // Lo que llega con la página abierta se ve en el momento: no se acumula
            MarkLogsSeen(persist: false);
        }

        var badge = LogUnread.Badge(LogUnread.Count(_logs.AllEntries, _logsSeenUntil));
        _logsItem.Count = badge.Text;
        _logsItem.CountTone = badge.Tone;
        _logsItem.CountToolTip = badge.ToolTip;
    }

    // La marca solo avanza (nunca retrocede, p. ej. al elegir un fichero de otro día)
    private void MarkLogsSeen(bool persist)
    {
        var seen = LogUnread.SeenUntil(_logs.AllEntries, DateTimeOffset.Now);
        if (_logsSeenUntil is null || seen > _logsSeenUntil)
        {
            _logsSeenUntil = seen;
        }

        if (persist && _logsSeenUntil is { } value)
        {
            LogSeenStore.Save(AppPaths.LogsSeenFile, value);
        }
    }

    public ObservableCollection<NavItem> MenuItems { get; }

    public ObservableCollection<NavItem> FooterItems { get; }

    public AppPage CurrentPage { get; private set; }

    /// <summary>
    /// true cuando existe icono en la bandeja: cerrar la ventana solo la oculta.
    /// Sin bandeja, cerrar la ventana termina la aplicación.
    /// </summary>
    public bool TrayAvailable { get; set; }

    public void Navigate(AppPage page)
    {
        // Al entrar en «日志» y al salir de ella se da por visto todo lo cargado y se guarda la marca
        if (page == AppPage.Logs || CurrentPage == AppPage.Logs)
        {
            MarkLogsSeen(persist: true);
        }

        CurrentPage = page;
        RefreshLogsBadge();
        PageHost.Content = page switch
        {
            AppPage.Overview => _services.GetRequiredService<OverviewPage>(),
            AppPage.Config => _services.GetRequiredService<ConfigPage>(),
            AppPage.Logs => _services.GetRequiredService<LogsPage>(),
            AppPage.Rules => _services.GetRequiredService<RulesPage>(),
            _ => _services.GetRequiredService<SettingsPage>(),
        };

        // Refleja la página en la navegación sin volver a disparar la selección
        _syncingSelection = true;
        MenuList.SelectedItem = MenuItems.FirstOrDefault(i => i.Page == page);
        FooterList.SelectedItem = FooterItems.FirstOrDefault(i => i.Page == page);
        _syncingSelection = false;
    }

    /// <summary>Muestra un diálogo propio sobre toda la ventana hasta que <paramref name="closed"/> se complete.</summary>
    public async Task ShowOverlayAsync(FrameworkElement dialog, Task closed)
    {
        // Las capas se apilan: el asistente de configuración abre encima el de emparejamiento y,
        // al cerrarse este, vuelve a verse el asistente
        var previous = OverlayContent.Content;
        OverlayContent.Content = dialog;
        OverlayLayer.Visibility = Visibility.Visible;
        try
        {
            await closed;
        }
        finally
        {
            OverlayContent.Content = previous;
            OverlayLayer.Visibility = previous is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    // —— Asistente de configuración ——

    private bool _setupWizardOpen;

    // Tras «omitir» no se vuelve a abrir solo en esta sesión del gestor; al iniciar sesión otra vez, sí
    private bool _setupWizardDismissed;

    /// <summary>Abre el asistente (botón de la vista general o apertura automática); devuelve al cerrarse.</summary>
    public async Task ShowSetupWizardAsync()
    {
        if (_setupWizardOpen)
        {
            return;
        }

        _setupWizardOpen = true;
        try
        {
            ShowMainWindowCore();
            Navigate(AppPage.Overview);
            var wizard = new SetupWizardDialog(this, _services.GetRequiredService<ConfigViewModel>());
            await ShowOverlayAsync(wizard, wizard.Completion);
            if (!wizard.Completed)
            {
                _setupWizardDismissed = true;
            }
        }
        finally
        {
            _setupWizardOpen = false;
        }
    }

    /// <summary>
    /// Apertura automática: solo mientras no haya dispositivo (el servicio no vigila nada). Sin contraseña
    /// no se insiste, porque «solo bloquear» puede ser una elección; lo recuerdan la bandeja y la vista general.
    /// Se lee la configuración del disco: funciona aunque el servicio aún no haya arrancado.
    /// </summary>
    public void OfferSetupWizardIfNeeded()
    {
        if (_setupWizardOpen || _setupWizardDismissed || OverlayContent.Content is not null)
        {
            return;
        }

        SetupState state;
        try
        {
            state = SetupCheck.Evaluate(ConfigDocument.Load(AppPaths.ConfigFile));
        }
        catch
        {
            // Sin configuración legible no hay nada que ofrecer: la página Configuración explica el problema
            return;
        }

        if (state == SetupState.NoDevice)
        {
            _ = ShowSetupWizardAsync();
        }
    }

    /// <summary>Restaura y trae al frente la ventana (bandeja, segunda instancia).</summary>
    public void ShowMainWindow()
    {
        ShowMainWindowCore();
        OfferSetupWizardIfNeeded();
    }

    private void ShowMainWindowCore()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    /// <summary>Salida real (solo desde el menú de la bandeja o sin bandeja).</summary>
    public void ExitApplication()
    {
        _exiting = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting && TrayAvailable)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Application.Current.Shutdown();
    }

    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || sender is not ListBox { SelectedItem: NavItem item })
        {
            return;
        }

        Navigate(item.Page);
    }
}

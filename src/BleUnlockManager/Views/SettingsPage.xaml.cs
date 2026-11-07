using System.Diagnostics;
using System.Windows.Controls;
using BleUnlockManager.Core.Localization;
using BleUnlockManager.Core.Resources;
using BleUnlockManager.ViewModels;

namespace BleUnlockManager.Views;

public partial class SettingsPage : UserControl
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        ConfigLocation = new(() => UiStrings.Settings_ConfigFile, viewModel.ConfigFilePath);
        LogsLocation = new(() => UiStrings.Logs_ServiceLog, viewModel.LogsDirPath);
        InstallLocation = new(() => UiStrings.Settings_InstallDir, viewModel.InstallDirPath);
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }

    public FileLocation ConfigLocation { get; }

    public FileLocation LogsLocation { get; }

    public FileLocation InstallLocation { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await ViewModel.RefreshAutoStartAsync();

    // —— Acerca de ——

    private async void OnCopyCloneClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ViewModel.CloneCommand);
        }
        catch
        {
            // Portapapeles ocupado por otro programa: no se muestra «copiado»
            return;
        }

        CopiedText.Visibility = Visibility.Visible;
        await Task.Delay(TimeSpan.FromSeconds(2));
        CopiedText.Visibility = Visibility.Collapsed;
    }

    private void OnOpenIssuesClicked(object sender, RoutedEventArgs e) => OpenUrl(ViewModel.IssuesUrl);

    private void OnOpenRepoClicked(object sender, RoutedEventArgs e) => OpenUrl(ViewModel.RepositoryUrl);

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // Sin navegador predeterminado: no hay nada más que hacer desde aquí
        }
    }

    // Fichero: se abre la carpeta con él seleccionado; carpeta: se abre tal cual
    private void OnOpenPathClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: string path })
        {
            return;
        }

        try
        {
            var start = File.Exists(path)
                ? new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                : new ProcessStartInfo { FileName = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!, UseShellExecute = true };
            Process.Start(start);
        }
        catch
        {
            // Sin permiso o ruta inexistente: el explorador ya informa por sí mismo en la mayoría de casos
        }
    }

    /// <summary>Fila de «ubicación de los archivos»; el título sigue al idioma actual.</summary>
    public sealed class FileLocation : ObservableObject
    {
        private readonly Func<string> _title;

        public FileLocation(Func<string> title, string path)
        {
            _title = title;
            Path = path;
            ManagerLanguage.Changed += () => OnPropertyChanged(nameof(Title));
        }

        public string Title => _title();

        public string Path { get; }
    }
}

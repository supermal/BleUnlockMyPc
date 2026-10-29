using System.Diagnostics;
using System.Windows.Controls;
using AutoUnlockManager.ViewModels;

namespace AutoUnlockManager.Views;

public partial class SettingsPage : UserControl
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        ConfigLocation = new("配置文件", viewModel.ConfigFilePath);
        LogsLocation = new("服务日志", viewModel.LogsDirPath);
        InstallLocation = new("安装目录", viewModel.InstallDirPath);
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }

    public FileLocation ConfigLocation { get; }

    public FileLocation LogsLocation { get; }

    public FileLocation InstallLocation { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await ViewModel.RefreshAutoStartAsync();

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

    public sealed record FileLocation(string Title, string Path);
}

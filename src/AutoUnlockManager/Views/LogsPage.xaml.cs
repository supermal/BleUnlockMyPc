using System.Diagnostics;
using System.Text;
using System.Windows.Controls;
using System.Windows.Media;
using AutoUnlockManager.ViewModels;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using AutoUnlockManager.Core.Resources;

namespace AutoUnlockManager.Views;

/// <summary>
/// Registro del servicio con seguimiento «superpuesto»: al final de la lista las líneas nuevas
/// empujan la vista; al revisar el historial la posición se mantiene y aparece «回到最新».
/// </summary>
public partial class LogsPage : UserControl
{
    // Margen en píxeles para considerar que la lista está al final
    private const double BottomTolerance = 2;

    private ScrollViewer? _scroll;
    private bool _stickToBottom = true;

    public LogsPage(LogsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.ViewRebuilt += OnViewRebuilt;
    }

    public LogsViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Initialize();

        if (_scroll is null && FindScrollViewer(LogList) is { } scroll)
        {
            _scroll = scroll;
            _scroll.ScrollChanged += OnScrollChanged;
        }

        if (ViewModel.AutoScroll)
        {
            _scroll?.ScrollToEnd();
        }
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_scroll is null)
        {
            return;
        }

        if (e.ExtentHeightChange > 0 && _stickToBottom && ViewModel.AutoScroll)
        {
            // Llegaron líneas estando al final: la vista las sigue
            _scroll.ScrollToEnd();
            return;
        }

        _stickToBottom = _scroll.VerticalOffset >= _scroll.ScrollableHeight - BottomTolerance;
        ViewModel.IsAtBottom = _stickToBottom;
    }

    // Tras filtrar, recargar o recortar la lista se vuelve al final si el seguimiento está activo
    private void OnViewRebuilt()
    {
        if (ViewModel.AutoScroll)
        {
            Dispatcher.BeginInvoke(() => _scroll?.ScrollToEnd());
        }
    }

    private void OnJumpToLatestClicked(object sender, RoutedEventArgs e)
    {
        _stickToBottom = true;
        _scroll?.ScrollToEnd();
        ViewModel.PendingNewLines = 0;
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            ViewModel.RefreshFiles();
        }
        catch (Exception ex)
        {
            ViewModel.ShowBanner(InfoBarSeverity.Error, UiStrings.Logs_RefreshFailed, ex.Message);
        }
    }

    private void OnOpenFolderClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = ViewModel.OpenFolderPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ViewModel.ShowBanner(InfoBarSeverity.Error, UiStrings.OpenFolder_FailedTitle, ex.Message);
        }
    }

    private async void OnExportClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            FileName = $"autounlock-logs-{DateTime.Now:yyyyMMdd-HHmmss}.log",
            Filter = UiStrings.Logs_ExportFilter,
            DefaultExt = ".log",
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            var count = ViewModel.VisibleCount;
            await File.WriteAllTextAsync(dialog.FileName, ViewModel.BuildExportText(), new UTF8Encoding(false));
            ViewModel.ShowBanner(InfoBarSeverity.Success, UiStrings.Logs_ExportedTitle, string.Format(UiStrings.Logs_ExportedMessage, count, dialog.FileName));
        }
        catch (Exception ex)
        {
            ViewModel.ShowBanner(InfoBarSeverity.Error, UiStrings.Overview_ExportFailed, ex.Message);
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }

            if (FindScrollViewer(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}

using System.Windows.Controls;
using BleUnlockManager.ViewModels;

namespace BleUnlockManager.Views;

public partial class RulesPage : UserControl
{
    private Window? _window;

    public RulesPage(RulesViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        // Solo se lee la configuración y se sigue la telemetría mientras la página se ve:
        // IsVisible cubre el cambio de página y la ventana oculta; minimizada sigue «visible»
        IsVisibleChanged += (_, _) => UpdateActive();
        Loaded += (_, _) => AttachWindow();
    }

    public RulesViewModel ViewModel { get; }

    private void AttachWindow()
    {
        var window = Window.GetWindow(this);
        if (window != _window)
        {
            if (_window is not null)
            {
                _window.StateChanged -= OnWindowStateChanged;
            }

            _window = window;
            if (_window is not null)
            {
                _window.StateChanged += OnWindowStateChanged;
            }
        }

        UpdateActive();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e) => UpdateActive();

    private void UpdateActive() =>
        ViewModel.SetActive(IsVisible && _window?.WindowState != WindowState.Minimized);

    // Pulsar un estado del diagrama desplaza la página hasta el principio de su sección
    private void OnNodeClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int section }
            || SectionsList.ItemContainerGenerator.ContainerFromIndex(section) is not FrameworkElement target)
        {
            return;
        }

        var top = target.TransformToAncestor(PageRoot).Transform(new Point(0, 0)).Y;
        Scroller.ScrollToVerticalOffset(top + PageRoot.Margin.Top - 8);
    }
}

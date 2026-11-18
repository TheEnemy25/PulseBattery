using System.Windows;
using System.Windows.Data;
using PulseBattery.App.ViewModels;
using PulseBattery.App.Services;

namespace PulseBattery.App.Views;

public partial class Dashboard : Window
{
    private readonly DashboardViewModel _viewModel;
    public Dashboard(DashboardViewModel viewModel) { MenuResources.EnsureLoaded(); InitializeComponent(); DataContext = _viewModel = viewModel; }
    private void ToggleSettings(object sender, RoutedEventArgs e) => _viewModel.SettingsOpen = !_viewModel.SettingsOpen;
    private void Minimize(object sender, RoutedEventArgs e) => Close();
    private void HideWindow(object sender, RoutedEventArgs e) => Close();
    private void ExitApp(object sender, RoutedEventArgs e) => ((App)Application.Current).ExitApplication();
    protected override void OnClosed(EventArgs e)
    {
        DataContext = null;
        BindingOperations.ClearAllBindings(this);
        Content = null;
        base.OnClosed(e);
    }
    private void OnStateChanged(object? sender, EventArgs e) { if (WindowState == WindowState.Minimized) Close(); }
}

using System.ComponentModel;
using System.Windows;
using PulseBattery.App.ViewModels;

namespace PulseBattery.App.Views;

public partial class Dashboard : Window
{
    private readonly DashboardViewModel _viewModel;
    public Dashboard(DashboardViewModel viewModel) { InitializeComponent(); DataContext = _viewModel = viewModel; }
    private void ToggleSettings(object sender, RoutedEventArgs e) => _viewModel.SettingsOpen = !_viewModel.SettingsOpen;
    private void ToggleTaskbarPanel(object sender, RoutedEventArgs e) => _viewModel.TaskbarPanel = !_viewModel.TaskbarPanel;
    private void Minimize(object sender, RoutedEventArgs e) => Hide();
    private void HideWindow(object sender, RoutedEventArgs e) => Hide();
    private void ExitApp(object sender, RoutedEventArgs e) => ((App)Application.Current).ExitApplication();
    private void OnClosing(object? sender, CancelEventArgs e) { if (!((App)Application.Current).IsExiting) { e.Cancel = true; Hide(); } }
    private void OnStateChanged(object? sender, EventArgs e) { if (WindowState == WindowState.Minimized) { Hide(); WindowState = WindowState.Normal; } }
}

using System.Windows;
using System.Windows.Input;
using PulseBattery.App.ViewModels;

namespace PulseBattery.App.Views;

public partial class StartupPrompt : Window
{
    private readonly DashboardViewModel _viewModel;
    public StartupPrompt(DashboardViewModel viewModel) { InitializeComponent(); DataContext = _viewModel = viewModel; }
    private void Enable(object sender, RoutedEventArgs e) { if (_viewModel.SetStartupPreference(true)) DialogResult = true; }
    private void Decline(object sender, RoutedEventArgs e) { if (_viewModel.SetStartupPreference(false)) DialogResult = false; }
    private void Dismiss(object sender, RoutedEventArgs e) => Close();
    private void HandleKey(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { e.Handled = true; Close(); } }
    private void MoveWindow(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left && e.OriginalSource is not System.Windows.Controls.Button) DragMove(); }
}

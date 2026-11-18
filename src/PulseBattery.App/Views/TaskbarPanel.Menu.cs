using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Automation;
using PulseBattery.Core;
using PulseBattery.App.Localization;
using PulseBattery.App.Services;
using PulseBattery.App.ViewModels;
using PulseBattery.App.Controls;

namespace PulseBattery.App.Views;

public partial class TaskbarPanel : UserControl, IDisposable
{
    private readonly DashboardViewModel _viewModel;
    private readonly TaskbarMenuPlacement _placement;
    private readonly Action _open;
    private readonly MenuItem _iconMenu;
    private readonly MenuItem _hideDevice;
    private readonly MenuItem _dashboardMenu;
    private readonly MenuItem _refreshMenu;
    private readonly MenuItem _exitMenu;
    private readonly Separator _exitSeparator = new();
    private readonly Dictionary<string, MenuItem> _visibilityRows = new(StringComparer.Ordinal);
    private readonly DeviceMenuHeader _deviceHeader = new();
    private readonly Separator _headerSeparator = new();
    private readonly DeviceKindMenu _kindChoices;
    private DeviceViewModel? _selected;
    private double _horizontalAnchor;
    public TaskbarPanel(DashboardViewModel viewModel, Action open)
    {
        InitializeComponent(); DataContext = _viewModel = viewModel; _open = open;
        _placement = new(this);
        MouseLeftButtonUp += (_, _) => open();
        var menu = new ContextMenu { Style = (Style)FindResource("PulseContextMenu") };
        var dashboard = Item("Open", "menu-open"); dashboard.Click += (_, _) => open();
        var refresh = Item("RefreshMenu", "menu-refresh"); refresh.Command = viewModel.RefreshCommand;
        var exit = Item("Exit", "menu-exit"); exit.Click += (_, _) => ((App)Application.Current).ExitApplication();
        _dashboardMenu = dashboard; _refreshMenu = refresh; _exitMenu = exit;
        _hideDevice = Item("HideDevice", "menu-eye-off");
        _hideDevice.Click += (_, _) =>
        {
            var id = _selected?.Device.Id;
            ContextMenu!.IsOpen = false;
            if (id is not null) viewModel.SetDeviceTaskbarVisibility(id, false);
        };
        _iconMenu = Item("IconMenu", "menu-appearance");
        _kindChoices = new(_iconMenu, kind => { if (_selected is not null) viewModel.SetDeviceKind(_selected.Device.Id, kind); });
        _iconMenu.SubmenuOpened += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (_iconMenu.Template.FindName("PART_Popup", _iconMenu) is Popup { Child: FrameworkElement child }) _placement.Clamp(child);
            });
        _deviceHeader.SetDevice(viewModel.TaskbarDevices.FirstOrDefault());
        _headerSeparator.Visibility = _deviceHeader.Visibility;
        menu.Items.Add(_deviceHeader); menu.Items.Add(_headerSeparator);
        menu.Items.Add(dashboard); menu.Items.Add(refresh); menu.Items.Add(_iconMenu); menu.Items.Add(_hideDevice); menu.Items.Add(_exitSeparator); menu.Items.Add(exit);
        ContextMenu = menu;
        _placement.Configure(menu, () => _selected is null ? PulseAnchor.TranslatePoint(new Point(PulseAnchor.ActualWidth / 2, 0), this).X : _horizontalAnchor);
        viewModel.DevicesChanged += UpdateOpenMenu;
        viewModel.SettingsChanged += UpdateOpenMenu;
        ContextMenuOpening += (_, _) =>
        {
            _selected = null;
            for (var current = System.Windows.Input.Mouse.DirectlyOver as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is FrameworkElement { DataContext: DeviceViewModel device }) { _selected = device; break; }
                if (ReferenceEquals(current, this)) break;
            }
            _horizontalAnchor = System.Windows.Input.Mouse.GetPosition(this).X;
            PrepareMenu();
        };
    }
    public void OpenMenuForVerification(bool application = false)
    {
        PrepareMenuForDevice(application ? null : _viewModel.TaskbarDevices.FirstOrDefault());
        _horizontalAnchor = ActualWidth / 2;
        ContextMenu!.IsOpen = true;
    }
    public MenuItem IconSubmenu => _iconMenu;
    internal void PrepareMenuForDevice(DeviceViewModel? device) { _selected = device; PrepareMenu(); }
    private void OpenPulse(object sender, RoutedEventArgs e) { _open(); e.Handled = true; }
    public void OpenApplicationMenuAtPointer()
    {
        _selected = null;
        var light = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int value && value != 0;
        ApplySystemTheme(light); PrepareMenu();
        ContextMenu!.Placement = PlacementMode.MousePoint; ContextMenu.PlacementTarget = null;
        ContextMenu.IsOpen = true;
    }
    private void PrepareMenu()
    {
        var menu = ContextMenu!;
        menu.Resources["MenuSurface"] = BackgroundForMenu;
        menu.Resources["MenuText"] = Foreground;
        menu.Resources["MenuOutline"] = new SolidColorBrush(IsLightTheme ? Color.FromRgb(210, 210, 210) : Color.FromRgb(70, 70, 70));
        menu.Resources["MenuDivider"] = new SolidColorBrush(IsLightTheme ? Color.FromRgb(220, 220, 220) : Color.FromRgb(61, 61, 61));
        menu.Resources["MenuHover"] = new SolidColorBrush(IsLightTheme ? Color.FromRgb(230, 230, 230) : Color.FromRgb(56, 56, 56));
        var deviceMenu = _selected is not null;
        _iconMenu.Visibility = _hideDevice.Visibility = deviceMenu ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in new MenuItem[] { _dashboardMenu, _refreshMenu, _exitMenu })
            item.Visibility = deviceMenu ? Visibility.Collapsed : Visibility.Visible;
        _exitSeparator.Visibility = deviceMenu ? Visibility.Collapsed : Visibility.Visible;
        if (deviceMenu) _deviceHeader.SetDevice(_selected); else _deviceHeader.SetApplication();
        _headerSeparator.Visibility = _deviceHeader.Visibility;
        DeviceKind? configured = _selected is not null && _viewModel.Settings.DeviceKinds.TryGetValue(_selected.Device.Id, out var kind) ? kind : null;
        _kindChoices.SelectCurrent(configured);
        SynchronizeVisibilityRows();
    }
    private void UpdateOpenMenu()
    {
        if (ContextMenu?.IsOpen != true || _selected is not null) return;
        SynchronizeVisibilityRows();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (ContextMenu.IsOpen) _placement.Clamp(ContextMenu); });
    }
    private void SynchronizeVisibilityRows()
    {
        var menu = ContextMenu!;
        var devices = _selected is null ? _viewModel.Devices.ToArray() : [];
        if (!_visibilityRows.Keys.SequenceEqual(devices.Select(device => device.Device.Id)))
        {
            foreach (var row in _visibilityRows.Values) menu.Items.Remove(row);
            _visibilityRows.Clear();
            var index = menu.Items.IndexOf(_exitSeparator);
            foreach (var device in devices)
            {
                var id = device.Device.Id;
                var row = new ImmediateMenuItem { IsCheckable = true, StaysOpenOnClick = true, Icon = new DeviceGlyph { Kind = device.Kind } };
                AutomationProperties.SetAutomationId(row, "Taskbar.DeviceVisibility." + id);
                AutomationProperties.SetHelpText(row, device.Name);
                row.Click += (_, _) => _viewModel.SetDeviceTaskbarVisibility(id, row.IsChecked);
                _visibilityRows[id] = row; menu.Items.Insert(index++, row);
            }
        }
        foreach (var device in devices)
        {
            var row = _visibilityRows[device.Device.Id];
            row.Header = device.Name; row.IsChecked = device.ShowOnTaskbar;
            ((DeviceGlyph)row.Icon).Kind = device.Kind;
        }
    }
    public void Dispose() { _viewModel.DevicesChanged -= UpdateOpenMenu; _viewModel.SettingsChanged -= UpdateOpenMenu; if (ContextMenu is not null) ContextMenu.IsOpen = false; }
    private static MenuItem Item(string key, string? asset = null)
    {
        var item = new ImmediateMenuItem();
        AutomationProperties.SetAutomationId(item, "Taskbar." + key);
        if (asset is not null) item.Icon = new SvgIcon { Asset = asset };
        item.SetBinding(HeaderedItemsControl.HeaderProperty, new Binding($"[{key}]") { Source = Strings.Current });
        return item;
    }
    public Brush BackgroundForMenu { get; private set; } = new SolidColorBrush(Color.FromRgb(32, 32, 32));
    public bool IsLightTheme { get; private set; }
    public void ApplySystemTheme(bool light)
    {
        if (IsLightTheme == light && Foreground == (light ? Brushes.Black : Brushes.White)) return;
        IsLightTheme = light;
        Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : light ? Brushes.Black : Brushes.White;
        BackgroundForMenu = new SolidColorBrush(light ? Color.FromRgb(250, 250, 250) : Color.FromRgb(32, 32, 32));
    }
}

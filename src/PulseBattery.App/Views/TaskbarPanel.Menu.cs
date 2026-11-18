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
    public static readonly DependencyProperty IsStackedProperty = DependencyProperty.Register(nameof(IsStacked), typeof(bool), typeof(TaskbarPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure, (panel, _) => ((TaskbarPanel)panel).RefreshLayoutMode()));
    public bool IsStacked { get => (bool)GetValue(IsStackedProperty); set => SetValue(IsStackedProperty, value); }
    private readonly DashboardViewModel _viewModel;
    private readonly TaskbarMenuPlacement _placement;
    private readonly Action _open;
    private MenuItem _iconMenu = null!;
    private MenuItem _devicesMenu = null!;
    private MenuItem _dashboardMenu = null!;
    private MenuItem _refreshMenu = null!;
    private MenuItem _exitMenu = null!;
    private Separator _exitSeparator = null!;
    private readonly Dictionary<string, MenuItem> _visibilityRows = new(StringComparer.Ordinal);
    private ItemCollection? _visibilityHost;
    private DeviceMenuHeader _deviceHeader = null!;
    private Separator _headerSeparator = null!;
    private DeviceKindMenu _kindChoices = null!;
    private DeviceViewModel? _selected;
    private double _horizontalAnchor;
    public TaskbarPanel(DashboardViewModel viewModel, Action open)
    {
        InitializeComponent(); DataContext = _viewModel = viewModel;
        _open = open;
        _placement = new(this);
        MouseLeftButtonUp += (_, _) => open();
        viewModel.DevicesChanged += UpdateOpenMenu;
        viewModel.SettingsChanged += UpdateOpenMenu;
        ContextMenuOpening += (_, e) =>
        {
            var firstOpen = ContextMenu is null;
            EnsureMenu();
            _selected = null;
            for (var current = System.Windows.Input.Mouse.DirectlyOver as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is FrameworkElement { DataContext: DeviceViewModel device }) { _selected = device; break; }
                if (ReferenceEquals(current, this)) break;
            }
            _horizontalAnchor = System.Windows.Input.Mouse.GetPosition(this).X;
            PrepareMenu();
            if (firstOpen) { e.Handled = true; ContextMenu!.IsOpen = true; }
        };
    }
    private void EnsureMenu()
    {
        if (ContextMenu is not null) return;
        MenuResources.EnsureLoaded();
        var viewModel = _viewModel;
        _exitSeparator = new(); _headerSeparator = new(); _deviceHeader = new();
        var menu = new ContextMenu { Style = (Style)FindResource("PulseContextMenu") };
        var dashboard = Item("Open", "menu-open"); dashboard.Click += (_, _) => _open();
        var refresh = Item("RefreshMenu", "menu-refresh"); refresh.Command = viewModel.RefreshCommand;
        var exit = Item("Exit", "menu-exit"); exit.Click += (_, _) => ((App)Application.Current).ExitApplication();
        _dashboardMenu = dashboard; _refreshMenu = refresh; _exitMenu = exit;
        _devicesMenu = Item("TaskbarDevices", "menu-appearance");
        _iconMenu = Item("IconMenu", "menu-appearance");
        _kindChoices = new(_iconMenu, kind => { if (_selected is not null) viewModel.SetDeviceKind(_selected.Device.Id, kind); });
        foreach (var parent in new[] { _iconMenu, _devicesMenu })
            parent.SubmenuOpened += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (parent.Template.FindName("PART_Popup", parent) is Popup { Child: FrameworkElement child }) _placement.Clamp(child);
            });
        _deviceHeader.SetDevice(viewModel.TaskbarDevices.FirstOrDefault());
        _headerSeparator.Visibility = _deviceHeader.Visibility;
        menu.Items.Add(_deviceHeader); menu.Items.Add(_headerSeparator);
        menu.Items.Add(dashboard); menu.Items.Add(refresh); menu.Items.Add(_iconMenu); menu.Items.Add(_devicesMenu); menu.Items.Add(_exitSeparator); menu.Items.Add(exit);
        ContextMenu = menu;
        _placement.Configure(menu, () => _selected is null ? ActualWidth / 2 : _horizontalAnchor);
    }
    public void OpenMenuForVerification(bool application = false)
    {
        PrepareMenuForDevice(application ? null : _viewModel.TaskbarDevices.FirstOrDefault());
        _horizontalAnchor = ActualWidth / 2;
        ContextMenu!.IsOpen = true;
    }
    public MenuItem IconSubmenu { get { EnsureMenu(); return _iconMenu; } }
    public MenuItem DevicesSubmenu { get { EnsureMenu(); return _devicesMenu; } }
    internal void PrepareMenuForDevice(DeviceViewModel? device) { _selected = device; PrepareMenu(); }
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
        EnsureMenu();
        var menu = ContextMenu!;
        menu.Resources["MenuSurface"] = BackgroundForMenu;
        menu.Resources["MenuText"] = Foreground;
        menu.Resources["MenuOutline"] = new SolidColorBrush(IsLightTheme ? Color.FromRgb(210, 210, 210) : Color.FromRgb(70, 70, 70));
        menu.Resources["MenuDivider"] = new SolidColorBrush(IsLightTheme ? Color.FromRgb(220, 220, 220) : Color.FromRgb(61, 61, 61));
        menu.Resources["MenuHover"] = new SolidColorBrush(IsLightTheme ? Color.FromRgb(230, 230, 230) : Color.FromRgb(56, 56, 56));
        var deviceMenu = _selected is not null;
        _iconMenu.Visibility = _devicesMenu.Visibility = deviceMenu ? Visibility.Visible : Visibility.Collapsed;
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
        InvalidateMeasure();
        if (ContextMenu?.IsOpen != true) return;
        SynchronizeVisibilityRows();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!ContextMenu.IsOpen) return;
            _placement.Clamp(ContextMenu);
            if (_devicesMenu.IsSubmenuOpen && _devicesMenu.Template.FindName("PART_Popup", _devicesMenu) is Popup { Child: FrameworkElement child })
                _placement.Clamp(child);
        });
    }
    protected override Size MeasureOverride(Size constraint)
    {
        var size = base.MeasureOverride(constraint);
        // Item templates are first materialized during Measure. Apply the mode directly
        // so the native host can measure both widths without waiting for data triggers.
        if (!ApplyLayoutMode()) return size;
        InvalidateContentMeasure(this);
        return base.MeasureOverride(constraint);
    }
    private void RefreshLayoutMode() { ApplyLayoutMode(); InvalidateContentMeasure(this); }
    private static void InvalidateContentMeasure(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            InvalidateContentMeasure(VisualTreeHelper.GetChild(parent, index));
        if (parent is UIElement element) element.InvalidateMeasure();
    }
    private bool ApplyLayoutMode()
    {
        if (_viewModel is null) return false;
        var appearance = IsStacked ? _viewModel.CompactTaskbarAppearance : _viewModel.TaskbarAppearance;
        var changed = false;
        void Set(DependencyObject element, DependencyProperty property, object value)
        {
            if (Equals(element.GetValue(property), value)) return;
            element.SetCurrentValue(property, value); changed = true;
        }
        foreach (var presenter in Enumerable.Range(0, DeviceIndicators.Items.Count)
            .Select(index => DeviceIndicators.ItemContainerGenerator.ContainerFromIndex(index)).OfType<ContentPresenter>())
        {
            if (presenter.ContentTemplate is not { } template || VisualTreeHelper.GetChildrenCount(presenter) == 0) continue;
            if (template.FindName("InlineIndicator", presenter) is StackPanel inline)
                Set(inline, VisibilityProperty, IsStacked ? Visibility.Collapsed : Visibility.Visible);
            if (template.FindName("StackedIndicator", presenter) is StackPanel stacked)
                Set(stacked, VisibilityProperty, IsStacked ? Visibility.Visible : Visibility.Collapsed);
            if (template.FindName("DeviceContainer", presenter) is Border container)
                Set(container, Border.PaddingProperty, appearance.DevicePadding);
            if (template.FindName("DeviceDivider", presenter) is Border divider)
                Set(divider, HeightProperty, IsStacked ? appearance.StackedDividerHeight : appearance.DividerHeight);
        }
        return changed;
    }
    private void SynchronizeVisibilityRows()
    {
        var menu = ContextMenu!;
        var devices = _viewModel.Devices.ToArray();
        var host = _selected is null ? menu.Items : _devicesMenu.Items;
        if (!ReferenceEquals(_visibilityHost, host) || !_visibilityRows.Keys.SequenceEqual(devices.Select(device => device.Device.Id)))
        {
            if (_visibilityHost is not null) foreach (var row in _visibilityRows.Values) _visibilityHost.Remove(row);
            _visibilityRows.Clear();
            _visibilityHost = host;
            var index = ReferenceEquals(host, menu.Items) ? menu.Items.IndexOf(_exitSeparator) : 0;
            foreach (var device in devices)
            {
                var id = device.Device.Id;
                var row = new ImmediateMenuItem { IsCheckable = true, StaysOpenOnClick = true, Icon = new DeviceGlyph { Kind = device.Kind } };
                AutomationProperties.SetAutomationId(row, "Taskbar.DeviceVisibility." + id);
                AutomationProperties.SetHelpText(row, device.Name);
                row.Click += (_, _) => _viewModel.SetDeviceTaskbarVisibility(id, row.IsChecked);
                _visibilityRows[id] = row; host.Insert(index++, row);
            }
        }
        foreach (var device in devices)
        {
            var row = _visibilityRows[device.Device.Id];
            row.Header = $"{device.Name}  {device.Level}{device.Percent}"; row.IsChecked = device.ShowOnTaskbar;
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

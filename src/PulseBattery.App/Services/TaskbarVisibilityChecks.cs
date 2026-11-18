using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Automation;
using System.Windows.Interop;
using PulseBattery.App.Controls;
using PulseBattery.App.Localization;
using PulseBattery.App.ViewModels;
using PulseBattery.App.Views;
using PulseBattery.Core;
using PulseBattery.Infrastructure;

namespace PulseBattery.App.Services;

internal static class TaskbarVisibilityChecks
{
    public static async Task<bool> RunAsync(string folder)
    {
        var path = Path.Combine(folder, "isolated-visibility-settings.json");
        var store = new JsonSettingsStore(path);
        store.Save(new AppSettings { Language = Strings.Current.Language, DeviceOrder = ["visibility:headset", "visibility:mouse"] });
        var provider = new VisibilityProvider();
        var model = new DashboardViewModel(new BatteryMonitor([provider]), store, default);
        await model.RefreshAsync();
        using var panel = new TaskbarPanel(model, () => { });
        using var host = new HwndSource(new HwndSourceParameters("Pulse taskbar sizing checks")
        {
            WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080,
            PositionX = -32000, PositionY = -32000, Width = 420, Height = 48, UsesPerPixelOpacity = true
        });
        host.CompositionTarget.BackgroundColor = Colors.Transparent;
        host.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
        host.RootVisual = panel;
        await panel.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
        ArrangePanel(panel);
        var initiallyShown = model.TaskbarDevices.Count == 2 && model.Devices.All(device => device.ShowOnTaskbar);
        double X(FrameworkElement element) => element.TranslatePoint(new Point(), panel).X;
        var pulseOnRight = !model.PulseOnLeft && X(panel.PulseAnchor) >= X(panel.DeviceIndicators) + panel.DeviceIndicators.ActualWidth;
        var dividersBetweenDevices = VisibleDividers(panel) == 1;
        model.PulseOnLeft = true; panel.UpdateLayout();
        var pulseMovesLeft = X(panel.PulseAnchor) == 0 && X(panel.DeviceIndicators) >= panel.PulseAnchor.ActualWidth;
        var positionPersists = new DashboardViewModel(new BatteryMonitor([provider]), new JsonSettingsStore(path), default).PulseOnLeft;
        RenderPanel(panel, Path.Combine(folder, "compact-pulse-left.png"));
        model.PulseOnLeft = false; panel.UpdateLayout();
        var pulseMovesBackRight = X(panel.PulseAnchor) >= X(panel.DeviceIndicators) + panel.DeviceIndicators.ActualWidth;
        RenderPanel(panel, Path.Combine(folder, "compact-pulse-right.png"));
        var sizeWidths = new List<double>();
        var sizeDetails = new List<object>();
        var sizesApplyLive = true;
        foreach (var size in Enum.GetValues<TaskbarIndicatorSize>())
        {
            model.TaskbarSize = size;
            await panel.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            ArrangePanel(panel);
            sizeWidths.Add(panel.ActualWidth);
            sizeDetails.Add(new { Size = size.ToString(), panel.FontSize, ExpectedFont = model.TaskbarAppearance.FontSize,
                AnchorWidth = panel.PulseAnchor.ActualWidth, ExpectedAnchor = model.TaskbarAppearance.AnchorWidth, Dividers = VisibleDividers(panel),
                ReceivesInput = panel.InputHitTest(new Point(panel.ActualWidth - 2, panel.ActualHeight - 2)) is not null });
            sizesApplyLive &= panel.FontSize == model.TaskbarAppearance.FontSize &&
                Math.Abs(panel.PulseAnchor.ActualWidth - model.TaskbarAppearance.AnchorWidth) * VisualTreeHelper.GetDpi(panel).DpiScaleX <= 0.5001 && VisibleDividers(panel) == 1 &&
                panel.InputHitTest(new Point(panel.ActualWidth - 2, panel.ActualHeight - 2)) is not null;
            RenderPanel(panel, Path.Combine(folder, $"taskbar-size-{size}.png"));
        }
        sizesApplyLive &= sizeWidths[0] < sizeWidths[1] && sizeWidths[1] < sizeWidths[2];
        var sizePersists = new JsonSettingsStore(path).Load() is { TaskbarSize: TaskbarIndicatorSize.Large, PulseOnLeft: false };
        model.TaskbarSize = TaskbarIndicatorSize.Compact;
        await panel.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
        ArrangePanel(panel);
        panel.PrepareMenuForDevice(null);
        var redundantItemsRemoved = !panel.ContextMenu!.Items.OfType<MenuItem>().Any(item =>
            AutomationProperties.GetAutomationId(item) is "Taskbar.TogglePanel" or "Taskbar.Language");
        var applicationRows = panel.ContextMenu!.Items.OfType<MenuItem>().Where(item => AutomationProperties.GetAutomationId(item).StartsWith("Taskbar.DeviceVisibility.", StringComparison.Ordinal)).ToArray();
        var appListsAllDevices = applicationRows.Length == 2 && applicationRows.All(row => row.IsCheckable && row.IsChecked && row.StaysOpenOnClick && row.Icon is DeviceGlyph);
        var mouseRow = applicationRows.Single(row => AutomationProperties.GetAutomationId(row).EndsWith("visibility:mouse", StringComparison.Ordinal));
        mouseRow.IsChecked = false; mouseRow.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        panel.PrepareMenuForDevice(null);
        var menuHidesAndKeepsRow = model.TaskbarDevices.Count == 1 && panel.ContextMenu.Items.Contains(mouseRow) && !mouseRow.IsChecked;
        ArrangePanel(panel);
        var noDanglingDivider = VisibleDividers(panel) == 0;
        mouseRow.IsChecked = true; mouseRow.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        ArrangePanel(panel);
        dividersBetweenDevices &= VisibleDividers(panel) == 1;
        var menuRestoresDevice = model.TaskbarDevices.Count == 2 && store.Load().HiddenTaskbarDevices.Length == 0;
        panel.PrepareMenuForDevice(model.Devices.Single(device => device.Device.Id == "visibility:mouse"));
        var visibleDeviceActions = panel.ContextMenu.Items.OfType<MenuItem>().Where(item => item.Visibility == Visibility.Visible)
            .Select(AutomationProperties.GetAutomationId).Where(id => id.StartsWith("Taskbar.", StringComparison.Ordinal)).ToArray();
        var deviceMenuScoped = visibleDeviceActions.SequenceEqual(["Taskbar.IconMenu", "Taskbar.HideDevice"]);
        var mouse = model.Devices.Single(device => device.Device.Id == "visibility:mouse");
        mouse.ShowOnTaskbar = false;
        var toggleFiltersOnlyTaskbar = model.Devices.Count == 2 && !model.IsEmpty && model.TaskbarDevices.Count == 1 &&
            model.TaskbarDevices[0].Device.Id == "visibility:headset" && panel.DeviceIndicators.Items.Count == 1 && mouse.Device.IsLow(15);
        await model.RefreshAsync();
        var survivesRefresh = !model.Devices.Single(device => device.Device.Id == "visibility:mouse").ShowOnTaskbar && model.TaskbarDevices.Count == 1;
        var restarted = new DashboardViewModel(new BatteryMonitor([provider]), new JsonSettingsStore(path), default);
        await restarted.RefreshAsync();
        var survivesRestart = restarted.TaskbarDevices.Count == 1 && store.Load().HiddenTaskbarDevices.SequenceEqual(["visibility:mouse"]);
        provider.Connected = false;
        await restarted.RefreshAsync();
        provider.Connected = true;
        await restarted.RefreshAsync();
        var survivesReconnect = restarted.TaskbarDevices.Count == 1 && !restarted.Devices.Single(device => device.Device.Id == "visibility:mouse").ShowOnTaskbar;
        restarted.SetDeviceTaskbarVisibility("visibility:mouse", true);
        var restoresSavedOrder = restarted.TaskbarDevices.Select(device => device.Device.Id).SequenceEqual(["visibility:headset", "visibility:mouse"]) &&
            restarted.Devices.Single(device => device.Device.Id == "visibility:mouse").ShowOnTaskbar;
        foreach (var device in restarted.Devices.ToArray()) restarted.SetDeviceTaskbarVisibility(device.Device.Id, false);
        var canHideAll = restarted.TaskbarDevices.Count == 0 && restarted.Devices.Count == 2 && !restarted.IsEmpty;
        restarted.Devices.Single(device => device.Device.Id == "visibility:mouse").ShowOnTaskbar = true;
        var restoresFromApp = restarted.TaskbarDevices is [{ Name: "Test mouse" }] && store.Load().HiddenTaskbarDevices.SequenceEqual(["visibility:headset"]);
        restarted.LowBatteryThreshold = 25;
        restarted.SetDeviceKind("visibility:mouse", DeviceKind.Gamepad);
        var unrelatedSettingsPreserveHidden = store.Load().HiddenTaskbarDevices.SequenceEqual(["visibility:headset"]) && restarted.TaskbarDevices.Count == 1;
        var header = new DeviceMenuHeader(); header.SetDevice(restarted.TaskbarDevices[0]);
        var headerIdentifiesDevice = header.Header?.ToString() == "Test mouse" && header.Icon is DeviceGlyph { Kind: DeviceKind.Gamepad } && !header.IsEnabled && !header.IsHitTestVisible;
        var opens = 0;
        using var persistentPanel = new TaskbarPanel(restarted, () => opens++);
        host.RootVisual = persistentPanel;
        ArrangePanel(persistentPanel);
        await persistentPanel.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
        foreach (var device in restarted.Devices.ToArray()) restarted.SetDeviceTaskbarVisibility(device.Device.Id, false);
        await persistentPanel.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        ArrangePanel(persistentPanel);
        var anchorWidth = Math.Max(1, (int)Math.Ceiling(persistentPanel.DesiredSize.Width));
        persistentPanel.Arrange(new Rect(0, 0, anchorWidth, 48)); persistentPanel.UpdateLayout();
        var bitmap = new RenderTargetBitmap(anchorWidth, 48, 96, 96, PixelFormats.Pbgra32); bitmap.Render(persistentPanel);
        var pixels = new byte[anchorWidth * 48 * 4]; bitmap.CopyPixels(pixels, anchorWidth * 4, 0);
        var anchorPixels = Enumerable.Range(0, anchorWidth * 48).Count(index => pixels[index * 4 + 3] > 150);
        var anchorSurvivesHiddenDevices = restarted.TaskbarDevices.Count == 0 && anchorWidth == 28 && persistentPanel.PulseAnchor.Visibility == Visibility.Visible && anchorPixels > 15;
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(folder, "pulse-anchor-only.png"))) encoder.Save(stream);
        persistentPanel.PulseAnchor.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var anchorOpensApp = opens == 1;
        restarted.SetDeviceTaskbarVisibility("visibility:mouse", true);
        restarted.TaskbarPanel = false;
        persistentPanel.Measure(new Size(double.PositiveInfinity, 48)); persistentPanel.UpdateLayout();
        var anchorSurvivesGlobalHide = persistentPanel.DeviceIndicators.Visibility == Visibility.Collapsed && persistentPanel.PulseAnchor.Visibility == Visibility.Visible;
        provider.Connected = false; await restarted.RefreshAsync();
        var anchorSurvivesDisconnect = restarted.Devices.Count == 0 && persistentPanel.PulseAnchor.Visibility == Visibility.Visible;
        header.SetApplication();
        var applicationHeader = header.Header?.ToString() == "Pulse" && header.Icon is SvgIcon { Asset: "pulse-mark" } && !header.IsEnabled;
        var passed = redundantItemsRemoved && sizesApplyLive && sizePersists && pulseOnRight && pulseMovesLeft && positionPersists && pulseMovesBackRight && dividersBetweenDevices && noDanglingDivider && deviceMenuScoped && appListsAllDevices && menuHidesAndKeepsRow && menuRestoresDevice && applicationHeader && anchorSurvivesHiddenDevices && anchorOpensApp && anchorSurvivesGlobalHide && anchorSurvivesDisconnect && initiallyShown && toggleFiltersOnlyTaskbar && survivesRefresh && survivesRestart && survivesReconnect && restoresSavedOrder && canHideAll && restoresFromApp && unrelatedSettingsPreserveHidden && headerIdentifiesDevice;
        File.WriteAllText(Path.Combine(folder, "taskbar-visibility-checks.json"), JsonSerializer.Serialize(new
        {
            TestFixtures = true, Passed = passed, InitiallyShowsAllDevices = initiallyShown,
            RedundantMenuItemsRemoved = redundantItemsRemoved,
            ToggleFiltersTaskbarWithoutRemovingDeviceOrBatteryAlerts = toggleFiltersOnlyTaskbar,
            SurvivesRefresh = survivesRefresh, SurvivesRestart = survivesRestart, SurvivesReconnect = survivesReconnect,
            ReappearsInSavedOrder = restoresSavedOrder, CanHideAllWithoutEmptyingDashboard = canHideAll,
            CanRestoreFromDashboard = restoresFromApp, IconAndOtherSettingsKeepVisibility = unrelatedSettingsPreserveHidden,
            ReadOnlyHeaderShowsDeviceNameAndCurrentIcon = headerIdentifiesDevice,
            PulseAnchorRendersWithAllDevicesHidden = anchorSurvivesHiddenDevices, PulseAnchorOpensApp = anchorOpensApp,
            PulseAnchorSurvivesGlobalHide = anchorSurvivesGlobalHide, PulseAnchorSurvivesDisconnectedDevices = anchorSurvivesDisconnect,
            ApplicationHeaderUsesMatchingPulseSvg = applicationHeader,
            DeviceMenuContainsOnlyDeviceActions = deviceMenuScoped, PulseListsAllDevicesWithMatchingChecks = appListsAllDevices,
            MenuCanHideWithoutLosingDeviceRow = menuHidesAndKeepsRow, MenuCanRestoreDevice = menuRestoresDevice,
            PulseDefaultsToRight = pulseOnRight, PulseMovesLeftLive = pulseMovesLeft, PulsePositionPersists = positionPersists,
            PulseMovesRightLive = pulseMovesBackRight, DividerBetweenVisibleDevices = dividersBetweenDevices,
            NoDanglingDividerWithOneDevice = noDanglingDivider,
            TaskbarSizesApplyLiveWithoutLosingHitArea = sizesApplyLive, TaskbarSizePersists = sizePersists, SizeWidths = sizeWidths,
            SizeDetails = sizeDetails, AnchorOnlyWidth = anchorWidth, AnchorOnlyPixels = anchorPixels
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed;
    }

    private static void ArrangePanel(TaskbarPanel panel)
    {
        panel.Width = double.NaN;
        panel.Measure(new Size(double.PositiveInfinity, 48));
        panel.Width = Math.Ceiling(panel.DesiredSize.Width); panel.Height = 48;
        panel.Arrange(new Rect(0, 0, panel.Width, 48)); panel.UpdateLayout();
    }
    private static int VisibleDividers(TaskbarPanel panel) => Enumerable.Range(0, panel.DeviceIndicators.Items.Count)
        .Select(index => panel.DeviceIndicators.ItemContainerGenerator.ContainerFromIndex(index)).OfType<ContentPresenter>()
        .Count(presenter => presenter.ContentTemplate.FindName("DeviceDivider", presenter) is Border { Visibility: Visibility.Visible });
    private static void RenderPanel(TaskbarPanel panel, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth), 48, 96, 96, PixelFormats.Pbgra32); bitmap.Render(panel);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private sealed class VisibilityProvider : IBatteryProvider
    {
        public bool Connected { get; set; } = true;
        public Task<IReadOnlyList<BatteryDevice>> GetDevicesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<BatteryDevice>>(!Connected ? [] : [
            new("visibility:mouse", "Test mouse", DeviceKind.Mouse, 8, false, ReadingState.Available, "test", "test"),
            new("visibility:headset", "Test headset", DeviceKind.Headset, 80, false, ReadingState.Available, "test", "test")
        ]);
    }
}

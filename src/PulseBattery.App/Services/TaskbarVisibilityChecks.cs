using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
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
        async Task Arrange()
        {
            await panel.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            panel.Width = double.NaN;
            panel.Measure(new Size(double.PositiveInfinity, 48));
            panel.Width = Math.Ceiling(panel.DesiredSize.Width); panel.Height = 48;
            panel.Arrange(new Rect(0, 0, panel.Width, 48)); panel.UpdateLayout();
            await panel.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            panel.Width = double.NaN; panel.Measure(new Size(double.PositiveInfinity, 48));
            panel.Width = Math.Ceiling(panel.DesiredSize.Width);
            panel.Arrange(new Rect(0, 0, panel.Width, 48)); panel.UpdateLayout();
        }
        await Arrange();
        var originalDevices = model.Devices.ToArray();
        var originalContainers = Enumerable.Range(0, model.TaskbarDevices.Count)
            .Select(index => panel.DeviceIndicators.ItemContainerGenerator.ContainerFromIndex(index)).ToArray();
        var collectionChanges = 0;
        model.Devices.CollectionChanged += (_, _) => collectionChanges++;
        model.TaskbarDevices.CollectionChanged += (_, _) => collectionChanges++;
        foreach (var scale in new[] { 80, 124, 150, 100 })
        {
            model.ApplyDashboardSettings(model.Settings with { TaskbarScalePercent = scale, TaskbarSpacing = scale % 25,
                TaskbarCompactScalePercent = 230 - scale, TaskbarCompactSpacing = 24 - scale % 25 });
            model.ApplyDashboardState(model.Settings, model.Devices.Select(device => device.Device).ToArray(), false, "", DateTime.Now);
            await Arrange();
        }
        var appearanceKeepsExistingVisuals = collectionChanges == 0 && originalDevices.SequenceEqual(model.Devices) &&
            originalContainers.SequenceEqual(Enumerable.Range(0, model.TaskbarDevices.Count)
                .Select(index => panel.DeviceIndicators.ItemContainerGenerator.ContainerFromIndex(index)));
        var startsVisible = model.TaskbarDevices.Count == 2 && model.Devices.All(device => device.ShowOnTaskbar);
        var noPulseAnchor = !Children<Button>(panel).Any();
        var widths = new List<double>();
        var scalesApply = true;
        foreach (var scale in new[] { 80, 100, 127, 150 })
        {
            model.TaskbarScalePercent = scale;
            await Arrange();
            widths.Add(panel.ActualWidth);
            scalesApply &= panel.FontSize == 11 * scale / 100d &&
                panel.InputHitTest(new Point(panel.ActualWidth - 2, 2)) is not null;
        }
        scalesApply &= widths.Zip(widths.Skip(1)).All(pair => pair.First < pair.Second);
        model.TaskbarSpacing = 0; await Arrange(); var narrow = panel.ActualWidth;
        model.TaskbarSpacing = 24; await Arrange();
        var wide = panel.ActualWidth;
        var spacingApplies = wide > narrow + 30;
        var savedAppearance = store.Load() is { TaskbarScalePercent: 150, TaskbarSpacing: 24 };
        var spacingSamples = new List<object>();
        var spacingFollowsSettingsInBothLayouts = true;
        model.TaskbarScalePercent = 100;
        foreach (var stacked in new[] { false, true })
        {
            panel.IsStacked = stacked;
            var measured = new List<double>();
            foreach (var spacing in new[] { 0, 1, 2, 4, 10, 15, 24 })
            {
                if (stacked) model.TaskbarCompactSpacing = spacing; else model.TaskbarSpacing = spacing;
                await Arrange();
                measured.Add(panel.ActualWidth);
                var paddingMatches = Enumerable.Range(0, panel.DeviceIndicators.Items.Count)
                    .Select(index => panel.DeviceIndicators.ItemContainerGenerator.ContainerFromIndex(index)).OfType<ContentPresenter>()
                    .All(presenter => presenter.ContentTemplate.FindName("DeviceContainer", presenter) is Border container &&
                        container.Padding == new Thickness(spacing / 2d, 0, spacing / 2d, 0));
                spacingFollowsSettingsInBothLayouts &= paddingMatches;
                spacingSamples.Add(new { Stacked = stacked, Spacing = spacing, Width = panel.ActualWidth, PaddingMatches = paddingMatches });
            }
            // Allow subpixel rounding at adjacent values, but never silently cap larger spacing.
            spacingFollowsSettingsInBothLayouts &= measured[3] > measured[2] && measured[4] > measured[3] &&
                measured[5] > measured[4] && measured[6] > measured[5];
        }
        model.TaskbarScalePercent = 80; model.TaskbarSpacing = 10;
        model.TaskbarCompactScalePercent = 100; model.TaskbarCompactSpacing = 8;
        panel.IsStacked = false; await Arrange(); var separateHorizontalWidth = panel.ActualWidth;
        model.TaskbarCompactScalePercent = 150; model.TaskbarCompactSpacing = 24;
        await Arrange(); var compactDoesNotChangeHorizontal = panel.ActualWidth == separateHorizontalWidth;
        model.TaskbarCompactScalePercent = 100; model.TaskbarCompactSpacing = 8;
        panel.IsStacked = true; await Arrange(); var separateCompactWidth = panel.ActualWidth;
        model.TaskbarSpacing = 24; model.TaskbarScalePercent = 150;
        await Arrange();
        var profilesAreIndependent = compactDoesNotChangeHorizontal && panel.ActualWidth == separateCompactWidth &&
            store.Load() is { TaskbarScalePercent: 150, TaskbarSpacing: 24, TaskbarCompactScalePercent: 100, TaskbarCompactSpacing: 8 };
        model.TaskbarCompactSpacing = 24; panel.IsStacked = false;
        var stackedReadable = true;
        var stackedNarrower = true;
        provider.MouseLevel = 100; provider.MouseCharging = true;
        await model.RefreshAsync();
        foreach (var scale in new[] { 80, 100, 150 })
        {
            model.TaskbarScalePercent = scale;
            model.TaskbarCompactScalePercent = scale;
            panel.IsStacked = false; await Arrange(); var horizontalWidth = panel.ActualWidth;
            panel.IsStacked = true; await Arrange();
            stackedNarrower &= panel.ActualWidth < horizontalWidth;
            stackedReadable &= model.CompactTaskbarAppearance.StackedFontSize >= 11 && model.CompactTaskbarAppearance.StackedPercentFontSize >= 10;
            foreach (var presenter in Enumerable.Range(0, panel.DeviceIndicators.Items.Count)
                .Select(index => panel.DeviceIndicators.ItemContainerGenerator.ContainerFromIndex(index)).OfType<ContentPresenter>())
            {
                var level = (StackPanel)presenter.ContentTemplate.FindName("StackedLevel", presenter);
                var icons = (StackPanel)presenter.ContentTemplate.FindName("StackedIcons", presenter);
                var inline = (StackPanel)presenter.ContentTemplate.FindName("InlineIndicator", presenter);
                stackedReadable &= inline.Visibility == Visibility.Collapsed && level.IsVisible && icons.IsVisible &&
                    level.TranslatePoint(new Point(0, level.ActualHeight), panel).Y <= icons.TranslatePoint(new Point(), panel).Y + 0.01 &&
                    level.TranslatePoint(new Point(), panel).Y >= 0 &&
                    icons.TranslatePoint(new Point(0, icons.ActualHeight), panel).Y <= 48.01;
            }
            var pixelWidth = (int)Math.Ceiling(panel.ActualWidth);
            var bitmap = new RenderTargetBitmap(pixelWidth, 48, 96, 96, PixelFormats.Pbgra32);
            var background = new DrawingVisual();
            using (var dc = background.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(32, 32, 32)), null, new Rect(0, 0, pixelWidth, 48));
            bitmap.Render(background); bitmap.Render(panel);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var image = File.Create(Path.Combine(folder, $"stacked-taskbar-{scale}.png")); encoder.Save(image);
        }
        await model.RefreshAsync(); await Arrange();
        stackedReadable &= panel.IsStacked && panel.DesiredSize.Height <= 48;
        panel.IsStacked = false;
        model.TaskbarScalePercent = 150;
        provider.MouseLevel = 8; provider.MouseCharging = false;
        await model.RefreshAsync(); await Arrange();
        panel.PrepareMenuForDevice(null);
        var rows = panel.ContextMenu!.Items.OfType<MenuItem>().Where(item => AutomationProperties.GetAutomationId(item).StartsWith("Taskbar.DeviceVisibility.")).ToArray();
        var allDevicesInTrayMenu = rows.Length == 2 && rows.All(row => row.IsCheckable && row.IsChecked && row.StaysOpenOnClick && row.Icon is DeviceGlyph);
        var menuShowsBattery = rows.All(row => row.Header?.ToString()?.Contains('%') == true);
        var noRedundantItems = !panel.ContextMenu.Items.OfType<MenuItem>().Any(item => AutomationProperties.GetAutomationId(item) is "Taskbar.TogglePanel" or "Taskbar.Language");
        var mouseRow = rows.Single(row => AutomationProperties.GetAutomationId(row).EndsWith("visibility:mouse"));
        mouseRow.IsChecked = false; mouseRow.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        panel.PrepareMenuForDevice(null);
        var menuCanHide = model.TaskbarDevices.Count == 1 && panel.ContextMenu.Items.Contains(mouseRow) && !mouseRow.IsChecked && model.Devices.Count == 2;
        mouseRow.IsChecked = true; mouseRow.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var menuCanRestore = model.TaskbarDevices.Count == 2 && store.Load().HiddenTaskbarDevices.Length == 0;
        panel.PrepareMenuForDevice(model.Devices.Single(device => device.Device.Id == "visibility:mouse"));
        var deviceActions = panel.ContextMenu.Items.OfType<MenuItem>().Where(item => item.Visibility == Visibility.Visible)
            .Select(AutomationProperties.GetAutomationId).Where(id => id.StartsWith("Taskbar.")).ToArray();
        var scopedDeviceMenu = deviceActions.SequenceEqual(["Taskbar.IconMenu", "Taskbar.TaskbarDevices"]);
        var sharedRows = panel.DevicesSubmenu.Items.OfType<MenuItem>().ToArray();
        var headsetRow = sharedRows.Single(row => AutomationProperties.GetAutomationId(row).EndsWith("visibility:headset"));
        headsetRow.IsChecked = false; headsetRow.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        panel.PrepareMenuForDevice(model.Devices.Single(device => device.Device.Id == "visibility:mouse"));
        var sharedMenuCanHideOtherDevice = sharedRows.Length == 2 && sharedRows.All(row => row.IsCheckable && row.StaysOpenOnClick) &&
            model.TaskbarDevices.Count == 1 && model.TaskbarDevices[0].Device.Id == "visibility:mouse" && !headsetRow.IsChecked;
        headsetRow.IsChecked = true; headsetRow.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var sharedMenuCanRestoreOtherDevice = model.TaskbarDevices.Count == 2 && store.Load().HiddenTaskbarDevices.Length == 0;
        model.Devices.Single(device => device.Device.Id == "visibility:mouse").ShowOnTaskbar = false;
        await model.RefreshAsync();
        var survivesRefresh = model.TaskbarDevices.Count == 1 && !model.Devices.Single(device => device.Device.Id == "visibility:mouse").ShowOnTaskbar;
        var restarted = new DashboardViewModel(new BatteryMonitor([provider]), new JsonSettingsStore(path), default);
        await restarted.RefreshAsync();
        var survivesRestart = restarted.TaskbarDevices.Count == 1 && restarted.TaskbarScalePercent == 150 && restarted.TaskbarSpacing == 24;
        provider.Connected = false; await restarted.RefreshAsync();
        provider.Connected = true; await restarted.RefreshAsync();
        var survivesReconnect = restarted.TaskbarDevices.Count == 1 && !restarted.Devices.Single(device => device.Device.Id == "visibility:mouse").ShowOnTaskbar;
        restarted.SetDeviceTaskbarVisibility("visibility:mouse", true);
        var restoresOrder = restarted.TaskbarDevices.Select(device => device.Device.Id).SequenceEqual(["visibility:headset", "visibility:mouse"]);
        foreach (var device in model.Devices.ToArray()) model.SetDeviceTaskbarVisibility(device.Device.Id, false);
        await Arrange();
        var noEmptyAnchor = panel.DesiredSize.Width == 0 && model.Devices.Count == 2;
        using var tray = new TrayService(model, () => { });
        tray.Update();
        var trayAvailableWithoutDevices = tray.IsVisible;
        model.SetDeviceTaskbarVisibility("visibility:mouse", true);
        model.TaskbarPanel = false; await Arrange();
        var globalHideKeepsTray = tray.IsVisible && panel.DeviceIndicators.Visibility == Visibility.Collapsed;
        var passed = profilesAreIndependent && appearanceKeepsExistingVisuals && spacingFollowsSettingsInBothLayouts && sharedMenuCanHideOtherDevice && sharedMenuCanRestoreOtherDevice && stackedReadable && stackedNarrower && startsVisible && noPulseAnchor && scalesApply && spacingApplies && savedAppearance && allDevicesInTrayMenu && menuShowsBattery && noRedundantItems && menuCanHide && menuCanRestore && scopedDeviceMenu && survivesRefresh && survivesRestart && survivesReconnect && restoresOrder && noEmptyAnchor && trayAvailableWithoutDevices && globalHideKeepsTray;
        File.WriteAllText(Path.Combine(folder, "taskbar-visibility-checks.json"), JsonSerializer.Serialize(new
        {
            Passed = passed, NoPulseAnchorOnTaskbar = noPulseAnchor, ScaleChangesSizeAndPreservesHitArea = scalesApply,
            AppearanceChangesKeepDeviceVisuals = appearanceKeepsExistingVisuals,
            LayoutProfilesHaveIndependentSizesAndSpacing = profilesAreIndependent,
            SpacingChangesWidth = spacingApplies, AppearancePersists = savedAppearance, Widths = widths,
            SpacingNarrowWidth = narrow, SpacingWideWidth = wide,
            SpacingFollowsSettingsInBothLayouts = spacingFollowsSettingsInBothLayouts, SpacingSamples = spacingSamples,
            StackedPercentagesAboveIconsRemainReadable = stackedReadable, StackedLayoutUsesLessWidth = stackedNarrower,
            MenuListsAllDevices = allDevicesInTrayMenu, MenuShowsBatteryLevels = menuShowsBattery,
            RedundantItemsRemoved = noRedundantItems, MenuCanHideDevice = menuCanHide, MenuCanRestoreDevice = menuCanRestore,
            DeviceMenuOffersSharedDeviceList = scopedDeviceMenu,
            SharedMenuCanHideOtherDevice = sharedMenuCanHideOtherDevice, SharedMenuCanRestoreOtherDevice = sharedMenuCanRestoreOtherDevice,
            VisibilitySurvivesRefresh = survivesRefresh,
            VisibilityAndAppearanceSurviveRestart = survivesRestart, VisibilitySurvivesReconnect = survivesReconnect,
            SavedDeviceOrderRestored = restoresOrder, NoUnusedTaskbarAreaWhenAllHidden = noEmptyAnchor,
            TrayRemainsAvailable = trayAvailableWithoutDevices && globalHideKeepsTray
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed;
    }
    private static IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Children<T>(child)) yield return descendant;
        }
    }
    private sealed class VisibilityProvider : IBatteryProvider
    {
        public bool Connected { get; set; } = true;
        public int MouseLevel { get; set; } = 8;
        public bool MouseCharging { get; set; }
        public Task<IReadOnlyList<BatteryDevice>> GetDevicesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<BatteryDevice>>(!Connected ? [] : [
            new("visibility:mouse", "Test mouse", DeviceKind.Mouse, MouseLevel, MouseCharging, ReadingState.Available, "test", "test"),
            new("visibility:headset", "Test headset", DeviceKind.Headset, 80, false, ReadingState.Available, "test", "test")
        ]);
    }
}

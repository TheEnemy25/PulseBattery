using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using PulseBattery.App.Localization;
using PulseBattery.App.ViewModels;
using PulseBattery.App.Views;
using PulseBattery.Core;
using PulseBattery.Infrastructure;

namespace PulseBattery.App.Services;

internal static class DeviceOrderChecks
{
    public static async Task<bool> RunAsync(string folder)
    {
        // Explicit fixtures and a separate settings file: never reorder the user's devices.
        var settingsPath = Path.Combine(folder, "isolated-order-settings.json");
        var store = new JsonSettingsStore(settingsPath);
        store.Save(new AppSettings { Language = Strings.Current.Language });
        var provider = new OrderProvider();
        var originalDevices = provider.Devices;
        var viewModel = new DashboardViewModel(new BatteryMonitor([provider]), store, default);
        await viewModel.RefreshAsync();
        var dashboard = new Dashboard(viewModel);
        var panel = new TaskbarPanel(viewModel, () => { });
        var content = (FrameworkElement)dashboard.Content;
        content.Measure(new Size(960, 690)); content.Arrange(new Rect(0, 0, 960, 690)); content.UpdateLayout();
        panel.Measure(new Size(420, 48)); panel.Arrange(new Rect(0, 0, 420, 48)); panel.UpdateLayout();
        var changed = 0;
        viewModel.DevicesChanged += () => changed++;
        var moved = viewModel.MoveDevice("order:gamepad", "order:mouse");
        string[] Ids(DashboardViewModel model) => model.Devices.Select(device => device.Device.Id).ToArray();
        var expected = new[] { "order:gamepad", "order:mouse", "order:headset" };
        var cardsMatchPanel = moved && changed == 1 && Ids(viewModel).SequenceEqual(expected) &&
            dashboard.DeviceCards.Items.Cast<DeviceViewModel>().Select(device => device.Device.Id).SequenceEqual(expected) &&
            panel.DeviceIndicators.Items.Cast<DeviceViewModel>().Select(device => device.Device.Id).SequenceEqual(expected);
        await viewModel.RefreshAsync();
        var survivesRefresh = Ids(viewModel).SequenceEqual(expected);
        var restarted = new DashboardViewModel(new BatteryMonitor([provider]), new JsonSettingsStore(settingsPath), default);
        await restarted.RefreshAsync();
        var survivesRestart = Ids(restarted).SequenceEqual(expected);
        provider.Devices = originalDevices.Where(device => device.Id != "order:mouse").ToArray();
        await restarted.RefreshAsync();
        restarted.MoveDevice("order:headset", "order:gamepad");
        var remembersOffline = restarted.Settings.DeviceOrder.SequenceEqual(["order:headset", "order:mouse", "order:gamepad"]);
        provider.Devices = originalDevices.Select(device => device.Id == "order:headset" ? device with { Name = "Renamed audio" } : device).ToArray();
        await restarted.RefreshAsync();
        var reconnectsInPlace = Ids(restarted).SequenceEqual(["order:headset", "order:mouse", "order:gamepad"]);
        provider.Devices = [.. provider.Devices, new("order:new", "New keyboard", DeviceKind.Keyboard, 60, false, ReadingState.Available, "test", "test")];
        await restarted.RefreshAsync();
        var withNewDevice = Ids(restarted);
        var appendsNew = withNewDevice.SequenceEqual(["order:headset", "order:mouse", "order:gamepad", "order:new"]);
        restarted.SetDeviceKind("order:headset", DeviceKind.Earbuds);
        restarted.LowBatteryThreshold = 25;
        var survivesSettings = Ids(restarted).SequenceEqual(withNewDevice) && new JsonSettingsStore(settingsPath).Load().DeviceKinds["order:headset"] == DeviceKind.Earbuds;
        var saved = File.ReadAllText(settingsPath);
        var rejectsInvalidMoves = !restarted.MoveDevice("missing", "order:mouse") && !restarted.MoveDevice("order:mouse", "missing") &&
            !restarted.MoveDevice("order:mouse", "order:mouse") && File.ReadAllText(settingsPath) == saved;
        var iconMenu = new ContextMenu();
        var iconChoices = new DeviceKindMenu(iconMenu, kind => viewModel.SetDeviceKind("order:mouse", kind));
        iconChoices.SelectCurrent(null);
        var automaticChecked = iconMenu.Items.OfType<MenuItem>().Single(item => item.Tag is null).IsChecked;
        var earbudChoice = iconMenu.Items.OfType<MenuItem>().Single(item => item.Tag is DeviceKind.Earbuds);
        earbudChoice.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var pickerUpdatesBothViews = automaticChecked &&
            dashboard.DeviceCards.Items.Cast<DeviceViewModel>().Single(device => device.Device.Id == "order:mouse").Kind == DeviceKind.Earbuds &&
            panel.DeviceIndicators.Items.Cast<DeviceViewModel>().Single(device => device.Device.Id == "order:mouse").Kind == DeviceKind.Earbuds &&
            store.Load().DeviceKinds["order:mouse"] == DeviceKind.Earbuds;
        iconChoices.SelectCurrent(DeviceKind.Earbuds);
        var selectionMatches = earbudChoice.IsChecked && iconMenu.Items.OfType<MenuItem>().Count(item => item.IsChecked) == 1;
        iconMenu.Items.OfType<MenuItem>().Single(item => item.Tag is null).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var pickerRestoresAutomatic = !store.Load().DeviceKinds.ContainsKey("order:mouse") && viewModel.Devices.Single(device => device.Device.Id == "order:mouse").Kind == DeviceKind.Mouse;
        var passed = pickerUpdatesBothViews && selectionMatches && pickerRestoresAutomatic && cardsMatchPanel && survivesRefresh && survivesRestart && remembersOffline && reconnectsInPlace && appendsNew && survivesSettings && rejectsInvalidMoves;
        File.WriteAllText(Path.Combine(folder, "device-order-checks.json"), JsonSerializer.Serialize(new
        {
            TestFixtures = true, Passed = passed, CardsAndTaskbarHaveSameOrder = cardsMatchPanel,
            SurvivesBatteryRefresh = survivesRefresh, SurvivesColdRestart = survivesRestart,
            KeepsOfflineDeviceSlot = remembersOffline, ReconnectAndRenameKeepPosition = reconnectsInPlace,
            NewDevicesAppend = appendsNew, OtherPreferencesPreserveOrder = survivesSettings,
            InvalidOrCanceledMovesDoNotSave = rejectsInvalidMoves, AfterFirstMove = expected, WithNewDevice = withNewDevice,
            SharedIconPickerUpdatesBothViewsAndSaves = pickerUpdatesBothViews,
            SelectedIconIsChecked = selectionMatches, IconPickerRestoresAutomatic = pickerRestoresAutomatic
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed;
    }

    private sealed class OrderProvider : IBatteryProvider
    {
        public IReadOnlyList<BatteryDevice> Devices { get; set; } = [
            new("order:mouse", "Test mouse", DeviceKind.Mouse, 64, false, ReadingState.Available, "test", "test"),
            new("order:headset", "Test headset", DeviceKind.Headset, 83, false, ReadingState.Available, "test", "test"),
            new("order:gamepad", "Test gamepad", DeviceKind.Gamepad, 50, false, ReadingState.Available, "test", "test")
        ];
        public Task<IReadOnlyList<BatteryDevice>> GetDevicesAsync(CancellationToken cancellationToken) => Task.FromResult(Devices);
    }
}

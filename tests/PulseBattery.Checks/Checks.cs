using System.Text.Json;
using PulseBattery.Core;
using PulseBattery.Infrastructure;
using PulseBattery.Infrastructure.Hid;
using PulseBattery.Infrastructure.Protocols;
using PulseBattery.Infrastructure.Providers;
using PulseBattery.Infrastructure.Bluetooth;

return await CheckRunner.RunAsync(args);

internal static class CheckRunner
{
    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            await ExecuteAsync(args);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Device check timed out or was canceled. No application crash occurred.");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Checks failed: {exception}");
            return 1;
        }
    }

    private static async Task ExecuteAsync(string[] args)
    {
        if (args.Contains("--windows"))
        {
            var catalog = new WindowsDeviceCatalog();
            var bluetooth = new ClassicBluetoothCatalog();
            Console.WriteLine(JsonSerializer.Serialize(new { Nodes = catalog.Enumerate(), Peers = bluetooth.Enumerate(), Devices = await new WindowsBatteryProvider(catalog, bluetooth).GetDevicesAsync(default) }, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        if (args.Contains("--controllers"))
        {
            var hid = new HidDeviceCatalog();
            var bluetooth = new ClassicBluetoothCatalog();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Hid = hid.Enumerate().Where(d => DeviceClassification.FromHid(d.ProductName, d.UsagePage, d.Usage) == DeviceKind.Gamepad),
                Bluetooth = bluetooth.Enumerate().Where(d => DeviceClassification.FromName(d.Name, d.ClassOfDevice) == DeviceKind.Gamepad),
                Readings = (await BatteryMonitorFactory.CreateDefault().ScanAsync(default)).Devices.Where(d => d.Kind == DeviceKind.Gamepad)
            }, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        if (args.Contains("--simulate-timeout"))
        {
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await new BatteryMonitor([new CancellableProvider()]).ScanAsync(canceled.Token);
            return;
        }
        if (args.Contains("--hardware"))
        {
            var catalog = new HidDeviceCatalog();
            Console.WriteLine(JsonSerializer.Serialize(catalog.Enumerate().Where(d => d.VendorId is 0x046D or 0x03F0), new JsonSerializerOptions { WriteIndented = true }));
            var monitor = BatteryMonitorFactory.CreateDefault(Console.Error.WriteLine);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(args.Contains("--repeat") ? 120 : 25));
            Console.WriteLine(JsonSerializer.Serialize(await monitor.ScanAsync(timeout.Token), new JsonSerializerOptions { WriteIndented = true }));
            if (args.Contains("--repeat"))
                for (var i = 0; i < 4; i++)
                    Console.WriteLine(JsonSerializer.Serialize(await monitor.ScanAsync(timeout.Token)));
            return;
        }

        await CheckBehaviorAsync();
    }

    private static async Task CheckBehaviorAsync()
    {
        var count = 0;
        void Check(bool valid, string label)
        {
            if (!valid) throw new InvalidOperationException("FAIL: " + label);
            Console.WriteLine("PASS: " + label);
            count++;
        }
        Check(BatteryReports.HyperXLevel([12, 2, 3, 1, 0, 6, 0], 6) == 0, "HyperX zero is valid");
        Check(BatteryReports.HyperXLevel([12, 2, 3, 1, 0, 6, 100], 6) == 100, "HyperX full battery");
        Check(BatteryReports.HyperXLevel([12, 2, 3, 1, 0, 6, 255], 6) is null, "HyperX sentinel rejected");
        Check(BatteryReports.HyperXLevel([12, 2, 3, 1, 0, 72, 1], 6) is null, "Unrelated reply rejected");
        Check(BatteryReports.HyperXLevel([12, 2], 6) is null, "Truncated reply rejected");
        Check(BatteryReports.IsLogitechReply([17, 1, 8, 27, 52, 0, 0], 1, 8, 27), "HID++ software ID matches");
        Check(!BatteryReports.IsLogitechReply([17, 1, 8, 26, 52, 0, 0], 1, 8, 27), "Another client reply rejected");
        Check(BatteryReports.LogitechBattery([17, 1, 8, 27, 35, 0, 1], true) == (35, true), "HID++ charging parsed");
        Check(BatteryReports.LogitechBattery([17, 1, 8, 27, 101, 0, 0], true).Level is null, "Invalid percentage rejected");
        Check(BatteryReports.LogitechBattery([17, 1, 8, 27, 0, 4, 0], true).Level is null, "Approximate HID++ level does not pretend to be zero percent");
        Check(BatteryReports.LogitechBattery([17, 1, 8, 27, 0, 0, 0], true).Level == 0, "Empty unified battery is zero percent");
        var device = new BatteryDevice("test", "Mouse", DeviceKind.Mouse, 8, false, ReadingState.Available, "test", "test");
        Check(device.IsLow(15), "Low battery alerts");
        Check(!(device with { IsCharging = true }).IsLow(15), "Charging does not alert");
        Check(!(device with { State = ReadingState.Stale }).IsLow(15), "Stale reading does not alert");
        var provider = new FakeProvider([device]);
        var monitorUnderTest = new BatteryMonitor([provider, new BrokenProvider()]);
        var first = await monitorUnderTest.ScanAsync(default);
        Check(first.Devices.Count == 1 && first.Errors.Count == 1, "Provider failures isolated");
        provider.Devices = [device with { BatteryPercent = null, State = ReadingState.Unavailable }];
        var second = await monitorUnderTest.ScanAsync(default);
        Check(second.Devices[0].State == ReadingState.Stale && second.Devices[0].BatteryPercent == 8, "Sleep retains timestamped reading");
        provider.Devices = [];
        Check((await monitorUnderTest.ScanAsync(default)).Devices.Count == 0, "Unplugged receiver disappears");
        Check(new AppSettings { RefreshSeconds = 1, LowBatteryThreshold = 150 }.Validate() is { RefreshSeconds: 15, LowBatteryThreshold: 40 }, "Settings ranges validated");
        var wireless = new HidDeviceInfo(@"\\?\hid#vid_046d&pid_c54d#receiver#{guid}", 0x046D, 0xC54D, "USB Receiver", 0xFF00, 2, 20, 20, [0x11]);
        var cable = wireless with { Path = @"\\?\hid#vid_046d&pid_c09b#cable#{guid}", ProductId = 0xC09B };
        var wirelessOnly = LogitechEndpoints.Select([wireless]);
        var whileCharging = LogitechEndpoints.Select([wireless, cable]);
        Check(whileCharging.Count == 1 && whileCharging[0].Device.ProductId == 0xC09B, "Charging cable replaces the duplicate receiver endpoint");
        Check(whileCharging[0].Id == wirelessOnly[0].Id, "Switching to the cable keeps a stable mouse identity");
        Check(LogitechEndpoints.Select([cable]).Count == 1, "Wired mouse works without its receiver");
        var secondMouse = wireless with { Path = @"\\?\hid#vid_046d&pid_c123#other-receiver#{guid}", ProductId = 0xC123 };
        Check(LogitechEndpoints.Select([wireless, secondMouse]).Count == 2, "Independent receivers are not merged by display name");
        Check(WindowsDeviceCatalog.ParseBattery(BitConverter.GetBytes(83u), 7) == 83, "Windows DWORD battery percentage");
        Check(WindowsDeviceCatalog.ParseBattery([0], 3) == 0, "Windows zero percent remains valid");
        Check(WindowsDeviceCatalog.ParseBattery([100], 3) == 100, "Windows byte battery percentage");
        Check(WindowsDeviceCatalog.ParseBattery([255], 3) is null, "Windows unknown sentinel is not a percentage");
        Check(WindowsDeviceCatalog.ParseBattery([2], 7) is null, "Malformed Windows property is ignored");
        Check(DeviceClassification.FromName("Unknown audio model") == DeviceKind.Other, "Unknown models do not need entries in a catalogue");
        Check(DeviceClassification.FromName("Galaxy Buds2 Pro") == DeviceKind.Earbuds, "Earbud classification is brand independent");
        Check(DeviceClassification.FromName("Unknown audio model", 0x240404) == DeviceKind.Headset, "Windows headset role supplies a fallback");
        Check(DeviceClassification.FromName("Unknown audio model", 0x240418) == DeviceKind.Headphones, "Windows headphone role supplies a fallback");
        Check(DeviceClassification.FromName("Wireless in-ear headphones", 0x240404) == DeviceKind.Earbuds, "General form description can refine a generic Bluetooth role");
        Check(DeviceClassification.FromName("Unknown name", 0x0540) == DeviceKind.Keyboard, "Bluetooth class identifies a keyboard");
        Check(DeviceClassification.FromName("Unknown name", 0x0580) == DeviceKind.Mouse, "Bluetooth class identifies a mouse");
        Check(DeviceClassification.FromName("OPPO Reno", 0x0200) == DeviceKind.Other, "An OPPO phone is not classified as earbuds");
        Check(DeviceClassification.FromName("Haylou W1", 0x240404) == DeviceKind.Headset, "Haylou has no per-model automatic exception");
        Check(DeviceClassification.FromName("OPPO Enco Air3", 0x240404) == DeviceKind.Headset, "OPPO has no per-model automatic exception");
        Check(DeviceClassification.FromName("Haylou S35 ANC", 0x240418) == DeviceKind.Headphones, "Haylou over-ear headphones are not classified by brand alone");
        Check(DeviceClassification.FromName("Haylou Solar", 0x0704) == DeviceKind.Other, "A Haylou wearable is not classified as earbuds");
        Check(DeviceClassification.FromName("Unknown input", 0x0504) == DeviceKind.Gamepad, "Bluetooth joystick class identifies a controller without its model name");
        Check(DeviceClassification.FromName("Unknown input", 0x0508) == DeviceKind.Gamepad, "Bluetooth gamepad class identifies a controller without its model name");
        Check(DeviceClassification.FromName("Unknown input", 0x050C) == DeviceKind.Other, "Bluetooth remote control is not mistaken for a gamepad");
        Check(DeviceClassification.FromName("Wireless Controller") == DeviceKind.Gamepad, "Generic controller description supplies a name fallback");
        Check(DeviceClassification.FromHid("Unknown input", 1, 4) == DeviceKind.Gamepad && DeviceClassification.FromHid("Unknown input", 1, 5) == DeviceKind.Gamepad, "HID joystick and gamepad usages identify the device kind");
        Check(DeviceClassification.FromHid("Wireless Controller", 1, 6) == DeviceKind.Keyboard, "HID keyboard function takes priority over an ambiguous name");
        Check(DeviceClassification.FromHid("Unknown input", 0xFF00, 5) == DeviceKind.Other, "Vendor usage five is not the standard gamepad usage");
        var btRoot = new WindowsDeviceNode("BTHENUM\\DEV_AABBCCDDEEFF", "Wireless earbuds", "Bluetooth", "container", null, "AABBCCDDEEFF");
        var btBatteryProfile = btRoot with { Id = "BTHENUM\\HFP", Name = "Hands-Free AG", BatteryPercent = 72, BluetoothAddress = null };
        var btPeer = new BluetoothPeer("AABBCCDDEEFF", "Wireless earbuds", 0x240404, true);
        var windowsReadings = WindowsBatteryProvider.MapDevices([btRoot, btBatteryProfile], [btPeer]);
        Check(windowsReadings.Count == 1 && windowsReadings[0].BatteryPercent == 72 && windowsReadings[0].Kind == DeviceKind.Earbuds, "Bluetooth audio profiles share one device and battery");
        var haylouRoot = btRoot with { Name = "Haylou W1" };
        var haylouProfile = btBatteryProfile with { Name = "Haylou W1 Hands-Free AG", BatteryPercent = 95 };
        var haylouPeer = btPeer with { Name = "Haylou W1", ClassOfDevice = 0x240404 };
        var haylouReadings = WindowsBatteryProvider.MapDevices([haylouRoot, haylouProfile], [haylouPeer]);
        Check(haylouReadings.Count == 1 && haylouReadings[0].Kind == DeviceKind.Headset && haylouReadings[0].BatteryPercent == 95, "An uncertain audio kind does not affect the battery reading");
        var renamedReadings = WindowsBatteryProvider.MapDevices([haylouRoot with { Name = "Renamed audio" }, haylouProfile], [haylouPeer with { Name = "Renamed audio" }]);
        Check(renamedReadings[0].Id == haylouReadings[0].Id, "Bluetooth identity survives a display name change");
        Check(WindowsBatteryProvider.MapDevices([btRoot, btBatteryProfile], [btPeer with { Connected = false }]).Count == 0, "Disconnected paired Bluetooth device is not shown as live");
        var noBattery = WindowsBatteryProvider.MapDevices([btRoot], [btPeer]);
        Check(noBattery.Count == 1 && noBattery[0].State == ReadingState.Unavailable && noBattery[0].BatteryPercent is null, "Connected earbuds remain visible when percentage is unsupported");
        var gamepadRoot = btRoot with { Name = "Wireless Controller" };
        var gamepadPeer = btPeer with { Name = "Wireless Controller", ClassOfDevice = 0x0508 };
        var gamepadReadings = WindowsBatteryProvider.MapDevices([gamepadRoot, btBatteryProfile with { BatteryPercent = 64 }], [gamepadPeer]);
        Check(gamepadReadings is [{ Kind: DeviceKind.Gamepad, BatteryPercent: 64, State: ReadingState.Available }], "Bluetooth gamepad uses the actual Windows battery percentage");
        var unknownGamepadBattery = WindowsBatteryProvider.MapDevices([gamepadRoot], [gamepadPeer]);
        Check(unknownGamepadBattery is [{ Kind: DeviceKind.Gamepad, BatteryPercent: null, State: ReadingState.Unavailable }], "A connected controller with unsupported battery reports stays visible without a fake percentage");
        Check(WindowsBatteryProvider.MapDevices([gamepadRoot], [gamepadPeer with { Connected = false }]).Count == 0, "A remembered disconnected gamepad is not shown as connected");
        var hidGamepad = unknownGamepadBattery[0] with { Id = "hid-battery:test-controller", Source = "HID battery" };
        var mergedGamepad = DeviceReconciliation.MergeTransports([hidGamepad, gamepadReadings[0]]);
        Check(mergedGamepad is [{ BatteryPercent: 64 }] && mergedGamepad[0].Id == hidGamepad.Id, "Windows gamepad battery replaces an unsupported HID reading without a duplicate");
        Check(DeviceReconciliation.MergeTransports([hidGamepad, unknownGamepadBattery[0]]).Count == 1, "An unambiguous gamepad pair stays one device when neither transport reports charge");
        var usbHeadset = new BatteryDevice("usb:headset", "HyperX Cloud III S Wireless", DeviceKind.Headset, null, null, ReadingState.Unavailable, "HyperX HID", "USB");
        var btHeadset = usbHeadset with { Id = "bluetooth:headset", BatteryPercent = 83, State = ReadingState.Available, Source = "Windows battery", Detail = "Bluetooth" };
        var reconciled = DeviceReconciliation.MergeTransports([usbHeadset, btHeadset]);
        Check(reconciled.Count == 1 && reconciled[0].BatteryPercent == 83 && reconciled[0].Id == usbHeadset.Id, "Bluetooth replaces the silent USB headset without a duplicate");
        var twoBluetooth = DeviceReconciliation.MergeTransports([usbHeadset, btHeadset, btHeadset with { Id = "bluetooth:second" }]);
        Check(twoBluetooth.Count == 3, "Ambiguous same-name Bluetooth devices are not merged");
        var hidCapability = new HidBatteryValue(2, 1, 6, 0x20, 0, 0, 100, 0, 0);
        Check(StandardHidBatteryProvider.Normalize(64, hidCapability) == 64, "Standard HID percent is accepted without a vendor protocol");
        Check(StandardHidBatteryProvider.Normalize(255, hidCapability) is null, "HID values outside the declared range are rejected");
        Check(StandardHidBatteryProvider.Normalize(20, hidCapability with { LogicalMax = 255 }) is null, "Undocumented HID units do not invent a percentage");
        Check(StandardHidBatteryProvider.Normalize(128, hidCapability with { LogicalMax = 255, PhysicalMax = 100 }) == 50, "HID scaling follows an explicit percent physical range");
        var orderedDevices = new[] { device with { Id = "order:a" }, device with { Id = "order:b" }, device with { Id = "order:c" } };
        Check(DeviceOrdering.Apply(orderedDevices, ["order:c", "order:a"]).Select(d => d.Id).SequenceEqual(["order:c", "order:a", "order:b"]), "Saved order takes priority and new devices append");
        Check(DeviceOrdering.Apply(orderedDevices.Reverse(), []).Select(d => d.Id).SequenceEqual(["order:c", "order:b", "order:a"]), "Without a saved order the existing scan order is preserved");
        Check(DeviceOrdering.MergeVisible(["a", "offline", "b", "c"], ["c", "b", "a", "new"]).SequenceEqual(["c", "offline", "b", "a", "new"]), "Reordering visible devices preserves an offline device's slot");
        Check(DeviceOrdering.MergeVisible(["a", "offline", "b"], []).SequenceEqual(["a", "offline", "b"]), "An empty scan cannot erase remembered positions");
        Check(new AppSettings { DeviceOrder = ["a", "", "a", "  ", "b"] }.Validate().DeviceOrder.SequenceEqual(["a", "b"]), "Invalid and duplicate saved device IDs are removed without changing order");
        Check(new AppSettings().StartWithWindows is null, "Windows startup is undecided by default, never silently enabled");
        Check(new AppSettings().HiddenTaskbarDevices.Length == 0, "Devices appear on the taskbar by default");
        Check(new AppSettings { HiddenTaskbarDevices = ["device:a", "", "device:a", " "] }.Validate().HiddenTaskbarDevices.SequenceEqual(["device:a"]), "Hidden taskbar preferences remove invalid and duplicate IDs");
        Check(WindowsStartupRegistration.BuildCommand(@"C:\Program Files\Pulse Battery\PulseBattery.exe") == "\"C:\\Program Files\\Pulse Battery\\PulseBattery.exe\" --autostart", "Startup quotes the executable path and requests background startup");
        var unsafeStartupPathRejected = false;
        try { WindowsStartupRegistration.BuildCommand("C:\\Pulse\"\\PulseBattery.exe"); }
        catch (ArgumentException) { unsafeStartupPathRejected = true; }
        Check(unsafeStartupPathRejected, "Startup rejects a path that could break command quoting");
        var folder = Path.Combine(Path.GetTempPath(), "PulseBatteryChecks-" + Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(folder, "settings.json");
        try
        {
            var store = new JsonSettingsStore(settingsPath);
            Check(store.Load().RefreshSeconds == 60, "Missing settings defaults");
            store.Save(new AppSettings { ShowTaskbarPanel = false, RefreshSeconds = 90, Language = "en", DeviceKinds = new() { ["test:buds"] = DeviceKind.Earbuds, ["test:controller"] = DeviceKind.Gamepad }, DeviceOrder = ["test:controller", "test:buds"] });
            Check(store.Load() is { ShowTaskbarPanel: false, RefreshSeconds: 90 }, "Settings round trip");
            Check(store.Load().DeviceKinds["test:buds"] == DeviceKind.Earbuds, "Manual device icon persists");
            File.SetLastWriteTimeUtc(settingsPath, DateTime.UtcNow.AddYears(-1));
            var restartedStore = new JsonSettingsStore(settingsPath);
            Check(restartedStore.Load().DeviceKinds["test:buds"] == DeviceKind.Earbuds, "A year-old choice survives a cold settings load");
            Check(restartedStore.Load().DeviceKinds["test:controller"] == DeviceKind.Gamepad, "Gamepad choice persists across a cold load without changing existing earbud choices");
            Check(restartedStore.Load().DeviceOrder.SequenceEqual(["test:controller", "test:buds"]), "Device order survives a year-old settings file and cold restart");
            restartedStore.Save(restartedStore.Load() with { HiddenTaskbarDevices = ["test:buds"] });
            Check(new JsonSettingsStore(settingsPath).Load().HiddenTaskbarDevices.SequenceEqual(["test:buds"]), "Per-device taskbar visibility persists across cold settings loads");
            restartedStore.Save(restartedStore.Load() with { PulseOnLeft = true });
            Check(new JsonSettingsStore(settingsPath).Load() is { PulseOnLeft: true, HiddenTaskbarDevices: ["test:buds"] }, "Pulse position persists without resetting device visibility");
            restartedStore.Save(restartedStore.Load() with { TaskbarSize = TaskbarIndicatorSize.Large });
            Check(new JsonSettingsStore(settingsPath).Load() is { TaskbarSize: TaskbarIndicatorSize.Large, PulseOnLeft: true, HiddenTaskbarDevices: ["test:buds"] }, "Taskbar size survives a cold load without changing device visibility or Pulse position");
            Check(new AppSettings { TaskbarSize = (TaskbarIndicatorSize)999 }.Validate().TaskbarSize == TaskbarIndicatorSize.Compact, "Invalid taskbar size falls back to compact");
            restartedStore.Save(restartedStore.Load() with { LowBatteryThreshold = 25 });
            Check(new JsonSettingsStore(settingsPath).Load().DeviceKinds["test:buds"] == DeviceKind.Earbuds, "Saving other preferences retains an absent device's choice");
            Check(store.Load().Language == "en", "Selected UI language persists");
            store.Save(store.Load() with { StartWithWindows = false });
            Check(new JsonSettingsStore(settingsPath).Load().StartWithWindows == false, "Declining Windows startup is remembered across launches");
            store.Save(store.Load() with { StartWithWindows = true });
            Check(new JsonSettingsStore(settingsPath).Load().StartWithWindows == true, "Explicit Windows startup consent is remembered");
            Check(new AppSettings { Language = "unknown" }.Validate().Language == "uk", "Unknown language falls back to Ukrainian");
            File.WriteAllText(settingsPath, "{\"ShowWidget\":true,\"ShowDeviceTrayIcons\":true}");
            Check(store.Load().ShowTaskbarPanel, "Old floating-widget settings enable the new taskbar panel");
            Check(store.Load().DeviceOrder.Length == 0, "Older settings without device order keep the default order");
            Check(store.Load().HiddenTaskbarDevices.Length == 0, "Older settings show all devices until explicitly hidden");
            Check(!store.Load().PulseOnLeft, "Older settings keep Pulse on the right");
            Check(store.Load().TaskbarSize == TaskbarIndicatorSize.Compact, "Older settings keep the compact taskbar size");
            Check(store.Load().StartWithWindows is null, "Older settings prompt for startup instead of assuming consent");
            File.WriteAllText(settingsPath, "{broken json");
            Check(store.Load().RefreshSeconds == 60, "Corrupt settings recover");
        }
        finally
        {
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
            if (Directory.Exists(folder)) Directory.Delete(folder);
        }
        Console.WriteLine($"{count} checks passed.");
    }
}

sealed class FakeProvider(IReadOnlyList<BatteryDevice> devices) : IBatteryProvider
{
    public IReadOnlyList<BatteryDevice> Devices { get; set; } = devices;
    public Task<IReadOnlyList<BatteryDevice>> GetDevicesAsync(CancellationToken cancellationToken) => Task.FromResult(Devices);
}
sealed class BrokenProvider : IBatteryProvider
{
    public Task<IReadOnlyList<BatteryDevice>> GetDevicesAsync(CancellationToken cancellationToken) => throw new IOException("Simulated failure");
}

sealed class CancellableProvider : IBatteryProvider
{
    public Task<IReadOnlyList<BatteryDevice>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<BatteryDevice>>([]);
    }
}

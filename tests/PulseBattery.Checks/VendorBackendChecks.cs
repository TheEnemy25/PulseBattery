using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using PulseBattery.Core;
using PulseBattery.Infrastructure.Hid;
using PulseBattery.Infrastructure.Providers;

internal static class VendorBackendChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        static object Device(string key, string name, string kind = "Mouse", int percent = 67,
            bool connected = true, bool asleep = false, int vid = 0, int pid = 0, string path = "", string level = "") =>
            new { key, name, kind, percent, charging = false, connected, asleep, vid, pid, path, level, via = "2.4 GHz receiver", detail = "" };
        static string Snapshot(params object[] devices) => JsonSerializer.Serialize(new { schema = 1, devices });
        static IReadOnlyList<BatteryDevice> Parse(params object[] devices) => VendorBatteryProvider.ParseSnapshot(Snapshot(devices), []);
        var models = new[] { "Razer Viper", "SteelSeries Aerox", "Corsair Dark Core", "Glorious Model O", "Pulsar X2", "VXE R1", "Keychron M3", "ASUS ROG Harpe", "LAMZU Maya", "Ninjutso Sora", "MCHOSE L7" };
        var broad = Parse(models.Select((name, i) => Device("fixture:" + i, name)).ToArray());
        check(broad.Count == models.Length && broad.All(d => d.Kind == DeviceKind.Mouse && d.BatteryPercent == 67 && d.BatterySupported), "Vendor backend readings are not restricted to the owner's brands or model names");
        check(Parse(Device("rk", "Royal Kludge", "Keyboard"), Device("aula", "AULA F75", "Keyboard"),
            Device("ds", "DualSense", "Gamepad"), Device("jabra", "Jabra", "Headset"), Device("buds", "Galaxy Buds", "Headset")).Select(d => d.Kind)
            .SequenceEqual([DeviceKind.Keyboard, DeviceKind.Keyboard, DeviceKind.Gamepad, DeviceKind.Headset, DeviceKind.Earbuds]), "Vendor keyboard, controller, headset and earbud roles reach Pulse correctly");
        check(Parse(Device("phone", "Phone", "Phone"), Device("mic", "Microphone", "Microphone"), Device("usb", "USB hub", "Other")).Count == 0,
            "Phone, microphone and arbitrary USB records cannot create battery cards");
        check(Parse(Device("zero", "Battery mouse", percent: 0)).Single() is { BatteryPercent: 0, State: ReadingState.Available }, "A real zero percent from a vendor device remains valid");
        check(Parse(Device("invalid", "Battery mouse", percent: 101)).Single() is { BatteryPercent: null, State: ReadingState.Unavailable }, "Invalid vendor percentages do not become fabricated battery values");
        check(Parse(Device("off", "Battery mouse", connected: false)).Count == 0, "A switched-off vendor device is not reported as connected");
        var asleep = Parse(Device("sleep", "Battery mouse", asleep: true)).Single();
        check(asleep.State == ReadingState.Unavailable && asleep.IsCharging is null, "Sleeping vendor devices use Pulse's last-reading policy");
        check(Parse(Device("level", "Controller", "Gamepad", percent: -1, level: "Low")).Single() is { BatteryPercent: null, ChargeLevel: BatteryChargeLevel.Low, State: ReadingState.Available },
            "A vendor coarse level is preserved without inventing an exact percentage");
        check(Parse(Device("xinput:0:0", "Xbox controller", "Gamepad", percent: 60, level: "Medium")).Single() is { Id: "xinput:0", BatteryPercent: null, ChargeLevel: BatteryChargeLevel.Medium },
            "Upstream XInput display estimates cannot appear as exact percentages and retain the original slot identity");
        check(Parse(Device("xbox:1234:0", "Xbox controller", "Gamepad", percent: 74)).Single() is { BatteryPercent: 74, ChargeLevel: null },
            "Windows.Gaming.Input reports retain their actual capacity-based percentage");
        check(Parse(Device("logi:old", "Logitech wireless device", percent: 50, level: "Good"),
            Device("switchpro:old", "Switch controller", "Gamepad", percent: 25, level: "Critical"))
            .All(d => d.BatteryPercent is null && d.ChargeLevel is not null), "Logitech and Switch coarse levels do not leak upstream display estimates as precise percentages");
        var mouse = new HidDeviceInfo("fixture-path", 0x1532, 1, "Mouse", 1, 2, 64, 64, [], ContainerId: "physical-one");
        check(VendorBatteryProvider.ParseSnapshot(Snapshot(Device("old-input", "Wireless device", "Other", vid: 0x1532, pid: 1)),
            [mouse, mouse with { Path = "aux-keyboard", Usage = 6 }]).Single().Kind == DeviceKind.Mouse,
            "A confirmed vendor battery reading survives an unknown model name and a receiver's auxiliary keyboard collection");
        var vendor = VendorBatteryProvider.ParseSnapshot(Snapshot(Device("razer", "Razer Viper", vid: 0x1532, pid: 1)), [mouse]).Single();
        var generic = new BatteryDevice("hid-battery:one:Mouse", "Mouse", DeviceKind.Mouse, null, null, ReadingState.Unavailable, "HID battery", "") { PhysicalId = "physical-one" };
        check(DeviceReconciliation.MergeTransports([vendor, generic]).Single() is { Name: "Razer Viper", BatteryPercent: 67 }, "A vendor and generic HID reading of the same mouse produce one card");
        var ambiguous = VendorBatteryProvider.ParseSnapshot(Snapshot(Device("razer", "Razer Viper", vid: 0x1532, pid: 1)), [mouse, mouse with { Path = "second-path", ContainerId = "physical-two" }]).Single();
        check(ambiguous.PhysicalId is null, "Identical devices on separate receivers are not joined by a guessed container");
        var exact = VendorBatteryProvider.ParseSnapshot(Snapshot(Device("razer", "Razer Viper", vid: 0x1532, pid: 1, path: "second-path")), [mouse, mouse with { Path = "second-path", ContainerId = "physical-two" }]).Single();
        check(exact.PhysicalId == "physical-two", "Exact driver HID paths disambiguate otherwise identical receivers");
        var oldMouse = new HidDeviceInfo("hid#vid_046d&pid_c54d#receiver&0", 0x046D, 0xC54D, "Mouse", 0xFF43, 0x0202, 20, 20, [0x11], ContainerId: "original-mouse");
        check(VendorBatteryProvider.ParseSnapshot(Snapshot(Device("logi:unit:123", "PRO X 2", vid: 0x046D, pid: 0xC54D)), [oldMouse]).Single().Id == "logitech:" + oldMouse.Path,
            "The original Logitech mouse keeps its saved taskbar and icon identity");
        var oldHeadset = new HidDeviceInfo("hid#vid_03f0&pid_06be#receiver&0&1", 0x03F0, 0x06BE, "Headset", 0xFF00, 1, 64, 64, [12], ContainerId: "original-headset");
        check(VendorBatteryProvider.ParseSnapshot(Snapshot(Device("hx", "HyperX Cloud III S Wireless", "Headset", vid: 0x03F0, pid: 0x06BE)), [oldHeadset]).Single().Id == "hyperx:receiver&0",
            "The original HyperX headset keeps its saved preferences after the backend upgrade");
        var rejectsSchema = false;
        try { VendorBatteryProvider.ParseSnapshot("{\"schema\":2,\"devices\":[]}", []); } catch (InvalidDataException) { rejectsSchema = true; }
        check(rejectsSchema, "Unknown backend protocol versions fail explicitly");
        var driverFailure = false;
        try { VendorBatteryProvider.ParseSnapshot("{\"schema\":1,\"error\":\"failure\",\"devices\":[]}", []); } catch (IOException) { driverFailure = true; }
        check(driverFailure, "A failed native scan is reported rather than silently becoming an empty device list");
        var cachePolicy = new VendorReadingCache();
        var now = DateTimeOffset.UtcNow;
        var reading = new VendorReading(vendor, "sleeping-mouse", 1800);
        var presentContainers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "physical-one" };
        cachePolicy.Update([reading], presentContainers, new HashSet<string>(), now);
        check(cachePolicy.Update([], presentContainers, new HashSet<string>(), now.AddMinutes(1)).Single() is { State: ReadingState.Unavailable, BatteryPercent: null },
            "An omitted sleeping device remains available for Pulse's stale-reading policy while its receiver is present");
        check(cachePolicy.Update([], new HashSet<string>(), new HashSet<string>(), now.AddMinutes(2)).Count == 0,
            "Unplugging the physical receiver removes a cached sleeping device immediately");
        cachePolicy.Update([reading], presentContainers, new HashSet<string>(), now);
        check(cachePolicy.Update([], presentContainers, new HashSet<string>(), now.AddMinutes(31)).Count == 0, "A driver's sleep grace period expires instead of retaining a phantom device forever");
        cachePolicy.Update([reading], presentContainers, new HashSet<string>(), now);
        check(cachePolicy.Update([], presentContainers, new HashSet<string> { reading.BackendKey }, now.AddSeconds(1)).Count == 0, "An explicitly disconnected device overrides the sleep grace period");

        var cache = Path.Combine(Path.GetTempPath(), "PulseBackendCheck-" + Guid.NewGuid().ToString("N"));
        var folder = VendorBatteryProvider.ExtractBackend(cache);
        var executable = Path.Combine(folder, "PulseBattery.DeviceBackend.exe");
        var originalBytes = File.ReadAllBytes(executable);
        File.WriteAllText(executable, "tampered cached helper");
        check(VendorBatteryProvider.ExtractBackend(cache) == folder && File.ReadAllBytes(executable).AsSpan().SequenceEqual(originalBytes), "A modified cached executable is restored from the verified embedded package");
        using (var source = ZipFile.OpenRead(Path.Combine(folder, "PulseBattery-DeviceBackend-Sources.zip")))
            check(source.GetEntry("native/WirelessBatteryBackend/bridge.cpp") is not null && source.GetEntry("scripts/Build-DeviceBackend.ps1") is not null && source.GetEntry("headsetcontrol-4.3.0.tar.gz") is not null,
                "Standalone builds include complete backend and HeadsetControl corresponding source");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("--version");
        using var process = Process.Start(start)!;
        var version = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        check(process.ExitCode == 0 && version.Contains("30419ed4e29ffd1b1337eccf367f2381faebd592", StringComparison.Ordinal), "The bundled native backend starts without an installed compiler or vendor software");
        // Delete only this test's own known files, never an application/user data folder.
        foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
        Directory.Delete(folder); Directory.Delete(cache);
    }
}

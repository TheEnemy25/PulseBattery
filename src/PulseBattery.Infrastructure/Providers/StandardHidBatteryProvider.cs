using PulseBattery.Core;
using PulseBattery.Infrastructure.Hid;

namespace PulseBattery.Infrastructure.Providers;

public sealed class StandardHidBatteryProvider(HidDeviceCatalog catalog) : IBatteryProvider
{
    public sealed record Candidate(HidDeviceInfo Device, DeviceKind Kind, string Id);

    public static bool HasBatteryCapability(HidDeviceInfo device) => device.BatteryValues?.Any(value =>
        value.UsagePage == 6 && value.Usage == 0x20 || value.UsagePage == 0x85 && value.Usage is 0x44 or 0x64 or 0x65) == true;

    public static IReadOnlyList<Candidate> DiscoverDevices(IReadOnlyList<HidDeviceInfo> interfaces)
    {
        var candidates = new List<Candidate>();
        foreach (var physical in interfaces.GroupBy(d => d.ContainerId ?? d.Path, StringComparer.OrdinalIgnoreCase))
        {
            var roles = physical.Select(d => DeviceClassification.FromHid(d.ProductName, d.UsagePage, d.Usage))
                .Where(k => k != DeviceKind.Other).Distinct().ToArray();
            var relevant = physical.Where(d => d.BatteryValues is { Count: > 0 } || d.UsagePage == 1 && d.Usage is 2 or 4 or 5 or 6 ||
                DeviceClassification.FromHid(d.ProductName, d.UsagePage, d.Usage) is DeviceKind.Headset or DeviceKind.Headphones or DeviceKind.Earbuds);
            foreach (var role in relevant.GroupBy(d =>
            {
                var kind = DeviceClassification.FromHid(d.ProductName, d.UsagePage, d.Usage);
                return kind == DeviceKind.Other && roles.Length == 1 ? roles[0] : kind;
            }))
            {
                var device = role.OrderByDescending(d => d.BatteryValues?.Count ?? 0).ThenBy(d => d.Path, StringComparer.OrdinalIgnoreCase).First();
                var id = device.ContainerId is { } container ? $"hid-battery:{container}:{role.Key}" : "hid-battery:" + device.Path;
                candidates.Add(new(device, role.Key, id));
            }
        }
        return candidates;
    }

    public Task<IReadOnlyList<BatteryDevice>> GetDevicesAsync(CancellationToken cancellationToken) => Task.Run<IReadOnlyList<BatteryDevice>>(() =>
    {
        var devices = new List<BatteryDevice>();
        foreach (var candidate in DiscoverDevices(catalog.Enumerate()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var device = candidate.Device;
            var name = device.ProductName.Length > 0 ? device.ProductName : $"HID {device.VendorId:X4}:{device.ProductId:X4}";
            var kind = candidate.Kind;
            BatteryDevice Reading(int? level, bool? charging) => new(candidate.Id, name, kind, level, charging,
                level.HasValue ? ReadingState.Available : ReadingState.Unavailable, "HID battery",
                level.HasValue ? "Стандартний HID Battery Strength" : "Пристрій не передав стандартний HID-рівень заряду")
                { PhysicalId = device.ContainerId, UsesXInput = device.Path.Contains("&IG_", StringComparison.OrdinalIgnoreCase), BatterySupported = HasBatteryCapability(device) };
            if (device.BatteryValues is not { Count: > 0 })
            {
                devices.Add(Reading(null, null));
                continue;
            }
            using var handle = NativeMethods.CreateFileW(device.Path, 0, 3, 0, 3, 0, 0);
            if (handle.IsInvalid || !NativeMethods.HidD_GetPreparsedData(handle, out var parsed)) { devices.Add(Reading(null, null)); continue; }
            try
            {
                int? level = null;
                bool? charging = null;
                foreach (var group in device.BatteryValues!.GroupBy(v => (v.ReportType, v.ReportId)))
                {
                    var length = group.Key.ReportType == 2 ? device.FeatureLength : device.InputLength;
                    if (length == 0) continue;
                    var report = new byte[length]; report[0] = group.Key.ReportId;
                    var success = group.Key.ReportType == 2 ? NativeMethods.HidD_GetFeature(handle, report, (uint)report.Length) : NativeMethods.HidD_GetInputReport(handle, report, (uint)report.Length);
                    if (!success) continue;
                    foreach (var value in group)
                    {
                        if (NativeMethods.HidP_GetUsageValue(value.ReportType, value.UsagePage, value.LinkCollection, value.Usage, out var raw, parsed, report, (uint)report.Length) < 0) continue;
                        if (value.UsagePage == 0x85 && value.Usage == 0x44) charging = raw == 1;
                        else level ??= Normalize(raw, value);
                    }
                }
                devices.Add(Reading(level, charging));
            }
            finally { NativeMethods.HidD_FreePreparsedData(parsed); }
        }
        return devices;
    }, cancellationToken);

    public static int? Normalize(uint value, HidBatteryValue capability)
    {
        if (value < capability.LogicalMin || value > capability.LogicalMax) return null;
        if (capability.LogicalMin == 0 && capability.LogicalMax == 100) return (int)value;
        // HID Battery Strength is a proportion of its declared logical range (USB HUT 0x06/0x20).
        if (capability.UsagePage == 6 && capability.Usage == 0x20 && capability.LogicalMin >= 0 && capability.LogicalMax > capability.LogicalMin)
            return (int)Math.Round((value - capability.LogicalMin) * 100d / (capability.LogicalMax - capability.LogicalMin));
        if (capability.PhysicalMin == 0 && capability.PhysicalMax == 100 && capability.LogicalMax > capability.LogicalMin)
            return (int)Math.Round((value - capability.LogicalMin) * 100d / (capability.LogicalMax - capability.LogicalMin));
        return null; // Do not invent a percentage for undocumented raw units.
    }
}

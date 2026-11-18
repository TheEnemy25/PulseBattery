using PulseBattery.Core;
using PulseBattery.Infrastructure.Hid;

namespace PulseBattery.Infrastructure.Providers;

public sealed class StandardHidBatteryProvider(HidDeviceCatalog catalog) : IBatteryProvider
{
    public Task<IReadOnlyList<BatteryDevice>> GetDevicesAsync(CancellationToken cancellationToken) => Task.Run<IReadOnlyList<BatteryDevice>>(() =>
    {
        var devices = new List<BatteryDevice>();
        foreach (var device in catalog.Enumerate().Where(d => d.BatteryValues?.Any(v => v.UsagePage == 6 || v.Usage == 0x64) == true || d.UsagePage == 1 && d.Usage is 4 or 5))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = device.ProductName.Length > 0 ? device.ProductName : $"HID {device.VendorId:X4}:{device.ProductId:X4}";
            var kind = DeviceClassification.FromHid(name, device.UsagePage, device.Usage);
            if (device.BatteryValues is not { Count: > 0 })
            {
                devices.Add(new("hid-battery:" + device.Path, name, kind, null, null, ReadingState.Unavailable, "HID battery", "Контролер не передав стандартний HID-рівень заряду"));
                continue;
            }
            using var handle = NativeMethods.CreateFileW(device.Path, 0, 3, 0, 3, 0, 0);
            if (handle.IsInvalid || !NativeMethods.HidD_GetPreparsedData(handle, out var parsed)) continue;
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
                devices.Add(new("hid-battery:" + device.Path, name, kind, level, charging, level.HasValue ? ReadingState.Available : ReadingState.Unavailable, "HID battery", "Стандартний HID Battery Strength"));
            }
            finally { NativeMethods.HidD_FreePreparsedData(parsed); }
        }
        return devices;
    }, cancellationToken);

    public static int? Normalize(uint value, HidBatteryValue capability)
    {
        if (value < capability.LogicalMin || value > capability.LogicalMax) return null;
        if (capability.LogicalMin == 0 && capability.LogicalMax == 100) return (int)value;
        if (capability.PhysicalMin == 0 && capability.PhysicalMax == 100 && capability.LogicalMax > capability.LogicalMin)
            return (int)Math.Round((value - capability.LogicalMin) * 100d / (capability.LogicalMax - capability.LogicalMin));
        return null; // Do not invent a percentage for undocumented raw units.
    }
}

namespace PulseBattery.Core;

public static class DeviceReconciliation
{
    public static IReadOnlyList<BatteryDevice> MergeTransports(IEnumerable<BatteryDevice> source)
    {
        var devices = source.ToList();
        foreach (var group in devices.GroupBy(d => NormalizeName(d.Name), StringComparer.OrdinalIgnoreCase).ToArray())
        {
            var bluetooth = group.Where(d => d.Id.StartsWith("bluetooth:", StringComparison.Ordinal)).ToArray();
            var receivers = group.Where(d => d.Source is "HyperX HID" or "HID++" or "HID battery").ToArray();
            // Only reconcile an unambiguous single transport pair, never several same-named devices.
            if (bluetooth.Length != 1 || receivers.Length != 1) continue;
            var wireless = bluetooth[0];
            var receiver = receivers[0];
            var preferred = receiver.State == ReadingState.Available ? receiver : wireless;
            if (preferred.State != ReadingState.Available && !(receiver.Kind == DeviceKind.Gamepad && wireless.Kind == DeviceKind.Gamepad)) continue;
            devices.Remove(wireless); devices.Remove(receiver);
            devices.Add(preferred with { Id = receiver.Id });
        }
        return devices;
    }
    private static string NormalizeName(string name)
    {
        var value = name.Trim();
        return value.StartsWith("Logitech ", StringComparison.OrdinalIgnoreCase) ? value[9..] : value;
    }
}

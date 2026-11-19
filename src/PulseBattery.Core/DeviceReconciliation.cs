namespace PulseBattery.Core;

public static class DeviceReconciliation
{
    public static IReadOnlyList<BatteryDevice> MergeTransports(IEnumerable<BatteryDevice> source)
    {
        var devices = source.ToList();
        foreach (var physical in devices.Where(d => !string.IsNullOrWhiteSpace(d.PhysicalId)).GroupBy(d => d.PhysicalId!, StringComparer.OrdinalIgnoreCase).ToArray())
        {
            var knownKinds = physical.Select(d => d.Kind).Where(k => k != DeviceKind.Other).Distinct().ToArray();
            foreach (var role in physical.GroupBy(d => d.Kind == DeviceKind.Other && knownKinds.Length == 1 ? knownKinds[0] : d.Kind))
            {
                var entries = role.ToArray();
                if (entries.Length < 2) continue;
                var vendor = entries.Where(d => d.Source is "HID++" or "HyperX HID" or "Wireless Battery").ToArray();
                // A shared receiver may represent several real devices of the same kind.
                if (vendor.Length > 1) continue;
                var identity = vendor.FirstOrDefault() ?? entries.FirstOrDefault(d => d.Source == "HID battery")
                    ?? entries.FirstOrDefault(d => d.Id.StartsWith("bluetooth:", StringComparison.Ordinal)) ?? entries[0];
                var reading = entries.OrderByDescending(d => d.State == ReadingState.Available)
                    .ThenByDescending(d => d.BatteryPercent.HasValue).ThenByDescending(d => d.Source is "HID++" or "HyperX HID" or "Wireless Battery").First();
                var name = vendor.FirstOrDefault()?.Name ?? entries.FirstOrDefault(d => d.Source == "Windows battery" && d.Id.StartsWith("bluetooth:", StringComparison.Ordinal))?.Name ?? identity.Name;
                foreach (var entry in entries) devices.Remove(entry);
                devices.Add(reading with { Id = identity.Id, Name = name, Kind = role.Key, UsesXInput = entries.Any(d => d.UsesXInput), BatterySupported = entries.Any(d => d.BatterySupported) });
            }
        }
        foreach (var group in devices.GroupBy(d => NormalizeName(d.Name), StringComparer.OrdinalIgnoreCase).ToArray())
        {
            var bluetooth = group.Where(d => d.Id.StartsWith("bluetooth:", StringComparison.Ordinal)).ToArray();
            var receivers = group.Where(d => d.Source is "HyperX HID" or "HID++" or "HID battery" or "Wireless Battery").ToArray();
            // Only reconcile an unambiguous single transport pair, never several same-named devices.
            if (bluetooth.Length != 1 || receivers.Length != 1) continue;
            var wireless = bluetooth[0];
            var receiver = receivers[0];
            var preferred = receiver.State == ReadingState.Available ? receiver : wireless;
            if (preferred.State != ReadingState.Available && !(receiver.Kind == DeviceKind.Gamepad && wireless.Kind == DeviceKind.Gamepad)) continue;
            devices.Remove(wireless); devices.Remove(receiver);
            devices.Add(preferred with { Id = receiver.Id, BatterySupported = receiver.BatterySupported || wireless.BatterySupported });
        }
        var xinput = devices.Where(d => d.Source == "XInput battery" && d.Kind == DeviceKind.Gamepad).ToArray();
        var xinputHid = devices.Where(d => d.Kind == DeviceKind.Gamepad && d.UsesXInput).ToArray();
        if (xinput.Length == 1 && xinputHid.Length == 1)
        {
            var identity = xinputHid[0];
            var reading = identity.State == ReadingState.Available ? identity : xinput[0];
            devices.Remove(identity); devices.Remove(xinput[0]);
            devices.Add(reading with { Id = identity.Id, Name = identity.Name, PhysicalId = identity.PhysicalId, UsesXInput = true, BatterySupported = identity.BatterySupported || reading.BatterySupported });
        }
        return devices;
    }
    private static string NormalizeName(string name)
    {
        var value = name.Trim();
        return value.StartsWith("Logitech ", StringComparison.OrdinalIgnoreCase) ? value[9..] : value;
    }
}

using PulseBattery.Core;
using PulseBattery.Infrastructure.Bluetooth;

namespace PulseBattery.Infrastructure.Providers;

public sealed class WindowsBatteryProvider(WindowsDeviceCatalog catalog, ClassicBluetoothCatalog bluetooth, Action<string>? trace = null) : IBatteryProvider
{
    public Task<IReadOnlyList<BatteryDevice>> GetDevicesAsync(CancellationToken cancellationToken) => Task.Run<IReadOnlyList<BatteryDevice>>(() =>
    {
        return ReadSources(() => catalog.Enumerate(cancellationToken), () => bluetooth.Enumerate(), trace);
    }, cancellationToken);

    public static IReadOnlyList<BatteryDevice> ReadSources(Func<IReadOnlyList<WindowsDeviceNode>> readNodes,
        Func<IReadOnlyList<BluetoothPeer>> readPeers, Action<string>? trace = null)
    {
        IReadOnlyList<WindowsDeviceNode> nodes = [];
        IReadOnlyList<BluetoothPeer> peers = [];
        var errors = new List<Exception>();
        try { nodes = readNodes(); }
        catch (System.ComponentModel.Win32Exception exception) { errors.Add(exception); trace?.Invoke("Windows device catalog: " + exception.Message); }
        try { peers = readPeers(); }
        catch (System.ComponentModel.Win32Exception exception) { errors.Add(exception); trace?.Invoke("Bluetooth device catalog: " + exception.Message); }
        if (errors.Count == 2) throw new AggregateException("Both Windows device catalogs failed.", errors);
        return MapDevices(nodes, peers);
    }

    public static IReadOnlyList<BatteryDevice> MapDevices(IReadOnlyList<WindowsDeviceNode> nodes, IReadOnlyList<BluetoothPeer> peers)
    {
        var results = new List<BatteryDevice>();
        foreach (var group in nodes.GroupBy(n => n.ContainerId))
        {
            var root = group.FirstOrDefault(n => n.BluetoothAddress is not null);
            var address = root?.BluetoothAddress;
            var peer = address is null ? null : peers.FirstOrDefault(p => p.Address == address);
            var battery = group.FirstOrDefault(n => n.BatteryPercent.HasValue)?.BatteryPercent;
            if (root is not null && peer is { Connected: false }) continue;
            if (root is null && battery is null)
            {
                // Keep role metadata for reconciling battery collections. Without battery
                // evidence these entries are removed by BatteryMonitor before reaching the UI.
                foreach (var input in group.Where(n => n.ClassName is "Mouse" or "Keyboard" || n.ClassName.Equals("MEDIA", StringComparison.OrdinalIgnoreCase)).GroupBy(n => n.ClassName))
                {
                    var item = input.First();
                    var inputKind = item.ClassName == "Mouse" ? DeviceKind.Mouse : item.ClassName == "Keyboard" ? DeviceKind.Keyboard : DeviceClassification.FromName(item.Name);
                    if (inputKind == DeviceKind.Other) inputKind = DeviceKind.Headset;
                    results.Add(new("windows:" + group.Key + ":" + inputKind, item.Name, inputKind, null, null,
                        ReadingState.Unavailable, "Windows battery", "Пристрій не передав рівень заряду Windows") { PhysicalId = group.Key });
                }
                continue;
            }
            var name = peer?.Name ?? root?.Name ?? group.First().Name;
            var kind = DeviceClassification.FromName(name, peer?.ClassOfDevice ?? 0);
            if (kind == DeviceKind.Other && (peer is null || peer.ClassOfDevice == 0))
            {
                if (group.Any(n => n.ClassName == "Mouse")) kind = DeviceKind.Mouse;
                else if (group.Any(n => n.ClassName == "Keyboard")) kind = DeviceKind.Keyboard;
                else if (group.Any(n => n.ClassName.Equals("MEDIA", StringComparison.OrdinalIgnoreCase))) kind = DeviceKind.Headset;
            }
            if (kind == DeviceKind.Other && battery is null) continue;
            // Bluetooth devices remain visible even if their firmware does not expose a percentage.
            var id = address is not null ? "bluetooth:" + address : "windows:" + group.Key;
            results.Add(new(id, name, kind, battery, null, battery.HasValue ? ReadingState.Available : ReadingState.Unavailable,
                "Windows battery", root is not null ? "Bluetooth · дані Windows" : "Дані заряду Windows") { PhysicalId = group.Key, BatterySupported = battery.HasValue || root is not null && kind != DeviceKind.Other });
        }
        foreach (var peer in peers.Where(p => p.Connected && results.All(d => d.Id != "bluetooth:" + p.Address)))
        {
            var kind = DeviceClassification.FromName(peer.Name, peer.ClassOfDevice);
            if (kind is DeviceKind.Other) continue;
            results.Add(new("bluetooth:" + peer.Address, peer.Name, kind, null, null, ReadingState.Unavailable, "Windows battery", "Bluetooth · пристрій не передав рівень заряду Windows") { BatterySupported = true });
        }
        return results;
    }
}

using PulseBattery.Core;
using PulseBattery.Infrastructure.Bluetooth;

namespace PulseBattery.Infrastructure.Providers;

public sealed class WindowsBatteryProvider(WindowsDeviceCatalog catalog, ClassicBluetoothCatalog bluetooth) : IBatteryProvider
{
    public Task<IReadOnlyList<BatteryDevice>> GetDevicesAsync(CancellationToken cancellationToken) => Task.Run<IReadOnlyList<BatteryDevice>>(() =>
    {
        var nodes = catalog.Enumerate(cancellationToken);
        var peers = bluetooth.Enumerate();
        return MapDevices(nodes, peers);
    }, cancellationToken);

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
            if (root is null && battery is null) continue;
            var name = peer?.Name ?? root?.Name ?? group.First().Name;
            var kind = DeviceClassification.FromName(name, peer?.ClassOfDevice ?? 0);
            if (kind == DeviceKind.Other && battery is null) continue;
            // Bluetooth devices remain visible even if their firmware does not expose a percentage.
            var id = address is not null ? "bluetooth:" + address : "windows:" + group.Key;
            results.Add(new(id, name, kind, battery, null, battery.HasValue ? ReadingState.Available : ReadingState.Unavailable,
                "Windows battery", root is not null ? "Bluetooth · дані Windows" : "Дані заряду Windows"));
        }
        foreach (var peer in peers.Where(p => p.Connected && results.All(d => d.Id != "bluetooth:" + p.Address)))
        {
            var kind = DeviceClassification.FromName(peer.Name, peer.ClassOfDevice);
            if (kind is DeviceKind.Other) continue;
            results.Add(new("bluetooth:" + peer.Address, peer.Name, kind, null, null, ReadingState.Unavailable, "Windows battery", "Bluetooth · пристрій не передав рівень заряду Windows"));
        }
        return results;
    }
}

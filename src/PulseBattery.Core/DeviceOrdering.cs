namespace PulseBattery.Core;

public static class DeviceOrdering
{
    public static IReadOnlyList<BatteryDevice> Apply(IEnumerable<BatteryDevice> devices, IReadOnlyList<string> savedOrder)
    {
        var ranks = savedOrder.Distinct(StringComparer.Ordinal).Select((id, index) => (id, index))
            .ToDictionary(pair => pair.id, pair => pair.index, StringComparer.Ordinal);
        // Unordered new devices follow the saved ones, retaining the provider's default order.
        return devices.OrderBy(device => ranks.GetValueOrDefault(device.Id, int.MaxValue)).ToArray();
    }

    public static string[] MergeVisible(IReadOnlyList<string> savedOrder, IEnumerable<string> visibleOrder)
    {
        var visible = visibleOrder.Distinct(StringComparer.Ordinal).ToArray();
        var visibleIds = visible.ToHashSet(StringComparer.Ordinal);
        var remaining = new Queue<string>(visible);
        // Reorder only occupied slots. Keep absent devices so reconnecting restores their place.
        return savedOrder.Concat(visible).Distinct(StringComparer.Ordinal)
            .Select(id => visibleIds.Contains(id) ? remaining.Dequeue() : id).ToArray();
    }
}

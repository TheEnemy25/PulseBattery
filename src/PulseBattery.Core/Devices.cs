namespace PulseBattery.Core;

public enum DeviceKind { Mouse, Headset, Headphones, Keyboard, Other, Earbuds, Gamepad }
public enum ReadingState { Available, Unavailable, Stale }

public sealed record BatteryDevice(
    string Id, string Name, DeviceKind Kind, int? BatteryPercent,
    bool? IsCharging, ReadingState State, string Source, string Detail,
    DateTimeOffset? LastSeen = null)
{
    public bool IsLow(int threshold) => State == ReadingState.Available &&
        BatteryPercent is not null && BatteryPercent <= threshold && IsCharging != true;
}

public sealed record ScanResult(IReadOnlyList<BatteryDevice> Devices, IReadOnlyList<string> Errors);

public interface IBatteryProvider
{
    Task<IReadOnlyList<BatteryDevice>> GetDevicesAsync(CancellationToken cancellationToken);
}

public interface IBatteryMonitor
{
    Task<ScanResult> ScanAsync(CancellationToken cancellationToken);
}

public sealed class BatteryMonitor(IEnumerable<IBatteryProvider> providers) : IBatteryMonitor
{
    private readonly IBatteryProvider[] _providers = providers.ToArray();
    private readonly Dictionary<string, BatteryDevice> _lastReadings = [];

    public async Task<ScanResult> ScanAsync(CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(_providers.Select(async provider =>
        {
            try { return (Devices: await provider.GetDevicesAsync(cancellationToken), Error: (string?)null); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { return (Devices: (IReadOnlyList<BatteryDevice>)[], Error: $"{provider.GetType().Name}: {exception.Message}"); }
        }));
        var devices = DeviceReconciliation.MergeTransports(results.SelectMany(result => result.Devices))
            .GroupBy(device => device.Id).Select(group => group.OrderByDescending(d => d.State == ReadingState.Available).First())
            .Select(device =>
            {
                if (device.State == ReadingState.Available && device.BatteryPercent is >= 0 and <= 100)
                {
                    var fresh = device with { LastSeen = DateTimeOffset.Now };
                    _lastReadings[device.Id] = fresh;
                    return fresh;
                }
                if (_lastReadings.TryGetValue(device.Id, out var previous) && DateTimeOffset.Now - previous.LastSeen < TimeSpan.FromMinutes(30))
                    return device with { Name = previous.Name, BatteryPercent = previous.BatteryPercent, LastSeen = previous.LastSeen, State = ReadingState.Stale, IsCharging = null };
                return device with { BatteryPercent = null, State = ReadingState.Unavailable };
            }).OrderBy(device => device.Kind).ThenBy(device => device.Name).ToArray();
        foreach (var key in _lastReadings.Where(pair => DateTimeOffset.Now - pair.Value.LastSeen >= TimeSpan.FromMinutes(30)).Select(pair => pair.Key).ToArray())
            _lastReadings.Remove(key);
        return new(devices, results.Where(result => result.Error is not null).Select(result => result.Error!).ToArray());
    }
}

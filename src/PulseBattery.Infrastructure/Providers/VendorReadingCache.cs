using PulseBattery.Core;

namespace PulseBattery.Infrastructure.Providers;

public sealed record VendorReading(BatteryDevice Device, string BackendKey, int GraceSeconds);

// Drivers may omit sleeping receiver devices. Honor their grace period only while the
// exact previously observed physical receiver is still present; never retain unplugged hardware.
public sealed class VendorReadingCache
{
    private readonly Dictionary<string, (VendorReading Reading, DateTimeOffset Seen)> _known = [];

    public IReadOnlyList<BatteryDevice> Update(IReadOnlyList<VendorReading> current,
        IReadOnlySet<string> presentContainers, IReadOnlySet<string> disconnectedKeys, DateTimeOffset now)
    {
        foreach (var key in disconnectedKeys) _known.Remove(key);
        var keys = current.Select(r => r.BackendKey).ToHashSet(StringComparer.Ordinal);
        foreach (var reading in current)
        {
            if (reading.GraceSeconds <= 0 || reading.Device.State != ReadingState.Available) continue;
            _known[reading.BackendKey] = (reading with { GraceSeconds = Math.Clamp(reading.GraceSeconds, 0, 1800) }, now);
        }
        var result = current.Select(r => r.Device).ToList();
        foreach (var (key, entry) in _known.ToArray())
        {
            if (keys.Contains(key)) continue;
            if (entry.Reading.Device.PhysicalId is not { } physical || !presentContainers.Contains(physical) ||
                now - entry.Seen >= TimeSpan.FromSeconds(entry.Reading.GraceSeconds)) { _known.Remove(key); continue; }
            result.Add(entry.Reading.Device with { BatteryPercent = null, ChargeLevel = null, IsCharging = null, State = ReadingState.Unavailable });
        }
        return result;
    }
}

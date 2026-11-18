using System.Text.Json.Serialization;
using PulseBattery.Core;

namespace PulseBattery.App.Services;

internal sealed record DashboardPacket
{
    public string Type { get; init; } = "";
    public AppSettings? Settings { get; init; }
    public BatteryDevice[]? Devices { get; init; }
    public bool Busy { get; init; }
    public string Error { get; init; } = "";
    public DateTime? LastRefresh { get; init; }
    public long Revision { get; init; }
    public long Acknowledged { get; init; }
}

[JsonSerializable(typeof(DashboardPacket))]
internal partial class DashboardJsonContext : JsonSerializerContext;

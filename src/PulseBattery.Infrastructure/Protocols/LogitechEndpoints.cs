using PulseBattery.Infrastructure.Hid;

namespace PulseBattery.Infrastructure.Protocols;

public sealed record LogitechEndpoint(HidDeviceInfo Device, string Id);

public static class LogitechEndpoints
{
    public static IReadOnlyList<LogitechEndpoint> Select(IEnumerable<HidDeviceInfo> devices)
    {
        var endpoints = devices.Where(d => d.VendorId == 0x046D && d.UsagePage >= 0xFF00 &&
            d.OutputReportIds.Contains((byte)0x11) && d.InputLength >= 20)
            .GroupBy(d => d.Path.Split('#')[2], StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
        // This model's cable and LIGHTSPEED receiver can coexist. While the cable is attached,
        // use its reading and retain the receiver's identity so sleep history cannot create a second card.
        var wiredProX2 = endpoints.Where(d => d.ProductId == 0xC09B).ToArray();
        var receiversProX2 = endpoints.Where(d => d.ProductId == 0xC54D).ToArray();
        var switchToCable = wiredProX2.Length == 1 && receiversProX2.Length == 1;
        return endpoints.Where(d => !switchToCable || d.ProductId != 0xC54D)
            .Select(d => new LogitechEndpoint(d, "logitech:" + (switchToCable && d.ProductId == 0xC09B ? receiversProX2[0].Path : d.Path)))
            .ToArray();
    }
}

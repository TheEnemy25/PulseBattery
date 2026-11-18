namespace PulseBattery.Infrastructure.Protocols;

public static class BatteryReports
{
    public static bool IsLogitechReply(byte[] report, byte device, byte feature, byte function) =>
        report.Length >= 7 && report[0] is 0x10 or 0x11 && report[1] == device && report[2] == feature && report[3] == function;

    public static int? HyperXLevel(byte[] report, byte command) =>
        report.Length >= 7 && report[0] == 0x0C && report[5] == command && report[6] <= 100 ? report[6] : null;

    public static (int? Level, bool? Charging) LogitechBattery(byte[] report, bool unified)
    {
        if (report.Length < 7 || report[4] > 100) return (null, null);
        if (report[4] == 0 && (!unified || report[5] != 0)) return (null, null);
        // Both x1000 and x1004 put percentage at parameter 0 and charge status at parameter 2.
        var status = report[6];
        bool? charging = status switch { 0 => false, 1 or 2 => true, 3 => false, _ => null };
        return (report[4], charging);
    }
}

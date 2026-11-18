using System.Runtime.InteropServices;
using PulseBattery.Core;

namespace PulseBattery.Infrastructure.Providers;

public sealed class XInputBatteryProvider : IBatteryProvider
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryInformation { public byte Type, Level; }
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
    private static extern uint GetBatteryInformation(uint userIndex, byte deviceType, out BatteryInformation information);

    public Task<IReadOnlyList<BatteryDevice>> GetDevicesAsync(CancellationToken cancellationToken) => Task.Run<IReadOnlyList<BatteryDevice>>(() =>
    {
        var result = new List<BatteryDevice>();
        for (uint slot = 0; slot < 4; slot++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GetBatteryInformation(slot, 0, out var battery) != 0) continue;
            var reading = MapBattery(slot, battery.Type, battery.Level);
            if (reading is not null) result.Add(reading);
        }
        return result;
    }, cancellationToken);

    public static BatteryDevice? MapBattery(uint slot, byte batteryType, byte batteryLevel)
    {
        if (batteryType is 0 or 1) return null; // Disconnected or wired: no battery is reported.
        var level = batteryType is 2 or 3 && batteryLevel <= 3 ? (BatteryChargeLevel?)batteryLevel : null;
        return new($"xinput:{slot}", $"XInput Controller {slot + 1}", DeviceKind.Gamepad, null, null,
            level.HasValue ? ReadingState.Available : ReadingState.Unavailable, "XInput battery", "Бездротовий контролер · рівень заряду XInput") { ChargeLevel = level, BatterySupported = batteryType is 2 or 3 };
    }
}

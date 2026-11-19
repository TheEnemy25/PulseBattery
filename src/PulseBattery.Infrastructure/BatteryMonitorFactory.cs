using PulseBattery.Core;
using PulseBattery.Infrastructure.Bluetooth;
using PulseBattery.Infrastructure.Hid;
using PulseBattery.Infrastructure.Providers;

namespace PulseBattery.Infrastructure;

public static class BatteryMonitorFactory
{
    public static IBatteryMonitor CreateDefault(Action<string>? trace = null)
    {
        var hid = new HidDeviceCatalog();
        return new BatteryMonitor([
            new VendorBatteryProvider(hid, trace),
            new WindowsBatteryProvider(new WindowsDeviceCatalog(), new ClassicBluetoothCatalog(), trace),
            new StandardHidBatteryProvider(hid),
            new BluetoothGattBatteryProvider(new WindowsDeviceCatalog(), trace)
        ]);
    }
}

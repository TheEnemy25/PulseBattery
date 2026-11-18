using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PulseBattery.Core;
using PulseBattery.Infrastructure.Bluetooth;
using PulseBattery.Infrastructure.Hid;

namespace PulseBattery.Infrastructure.Providers;

public sealed class BluetoothGattBatteryProvider(WindowsDeviceCatalog catalog, Action<string>? trace = null) : IBatteryProvider
{
    private static readonly Guid ServiceInterface = new("6e3bb679-4372-40c8-9eaa-4509df260cd8");
    private const string BatteryServiceUuid = "0000180f-0000-1000-8000-00805f9b34fb";
    private readonly SemaphoreSlim _scan = new(1, 1);

    public async Task<IReadOnlyList<BatteryDevice>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        // Native GATT reads may finish after a caller cancels. Do not overlap such scans.
        await _scan.WaitAsync(cancellationToken);
        try { return await Task.Run(() => ReadDevices(cancellationToken), cancellationToken); }
        finally { _scan.Release(); }
    }

    private IReadOnlyList<BatteryDevice> ReadDevices(CancellationToken cancellationToken)
    {
        var interfaces = catalog.EnumerateInterfaces(ServiceInterface, cancellationToken)
            .Concat(catalog.EnumerateInterfaces(new Guid(BatteryServiceUuid), cancellationToken))
            .Where(i => i.Path.Contains(BatteryServiceUuid, StringComparison.OrdinalIgnoreCase) || i.Node.Id.Contains(BatteryServiceUuid, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (interfaces.Length == 0) return [];
        IReadOnlyList<WindowsDeviceNode> nodes = [];
        try { nodes = catalog.Enumerate(cancellationToken); }
        catch (Win32Exception exception) { trace?.Invoke("GATT device names: " + exception.Message); }
        var devices = new List<BatteryDevice>();
        foreach (var item in interfaces.DistinctBy(i => i.Path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var handle = NativeMethods.CreateFileW(item.Path, 0x80000000, 3, 0, 3, 0, 0);
            if (handle.IsInvalid) { trace?.Invoke("GATT battery service could not be opened: " + Marshal.GetLastWin32Error()); continue; }
            var level = ReadBattery(handle);
            if (level is null) continue;
            var owner = nodes.FirstOrDefault(n => n.ContainerId == item.Node.ContainerId && n.BluetoothAddress is not null) ?? item.Node;
            var kind = DeviceClassification.FromName(owner.Name);
            var related = nodes.Where(n => n.ContainerId == owner.ContainerId).ToArray();
            if (related.Any(n => n.ClassName == "Mouse")) kind = DeviceKind.Mouse;
            else if (related.Any(n => n.ClassName == "Keyboard")) kind = DeviceKind.Keyboard;
            var address = owner.BluetoothAddress ?? item.Node.BluetoothAddress;
            var id = address is not null ? "bluetooth:" + address : "bluetooth-gatt:" + owner.ContainerId;
            devices.Add(new(id, owner.Name, kind, level, null, ReadingState.Available, "Bluetooth GATT",
                "Bluetooth LE · стандартний Battery Service") { PhysicalId = owner.ContainerId, BatterySupported = true });
        }
        return devices;
    }

    private int? ReadBattery(SafeFileHandle handle)
    {
        var status = BluetoothGATTGetCharacteristics(handle, 0, 0, 0, out var count, 0);
        if (count is 0 or > 128 || status != 0 && status != unchecked((int)0x800700EA)) return null;
        var buffer = Marshal.AllocHGlobal(count * Marshal.SizeOf<GattCharacteristic>());
        try
        {
            status = BluetoothGATTGetCharacteristics(handle, 0, count, buffer, out var actual, 0);
            if (status != 0 || actual > count) return null;
            for (var i = 0; i < actual; i++)
            {
                var characteristic = Marshal.PtrToStructure<GattCharacteristic>(buffer + i * Marshal.SizeOf<GattCharacteristic>());
                if (!characteristic.Uuid.Is(0x2A19) || characteristic.Readable == 0) continue;
                var value = new byte[8]; // ULONG DataSize followed by the one-byte Battery Level.
                status = BluetoothGATTGetCharacteristicValue(handle, ref characteristic, (uint)value.Length, value, out _, 4);
                if (status == 0 && BitConverter.ToUInt32(value) == 1) return ParseBatteryLevel(value.AsSpan(4, 1));
                trace?.Invoke($"GATT battery read: 0x{status:X8}");
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return null;
    }

    public static int? ParseBatteryLevel(ReadOnlySpan<byte> value) => value.Length == 1 && value[0] <= 100 ? value[0] : null;

    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct BluetoothUuid
    {
        [FieldOffset(0)] public byte IsShort;
        [FieldOffset(4)] public ushort Short;
        [FieldOffset(4)] public Guid Long;
        public readonly bool Is(ushort value) => IsShort != 0 ? Short == value : Long == new Guid($"{value:x8}-0000-1000-8000-00805f9b34fb");
    }

    [StructLayout(LayoutKind.Explicit, Size = 36)]
    private struct GattCharacteristic
    {
        [FieldOffset(0)] public ushort ServiceHandle;
        [FieldOffset(4)] public BluetoothUuid Uuid;
        [FieldOffset(24)] public ushort AttributeHandle;
        [FieldOffset(26)] public ushort ValueHandle;
        [FieldOffset(28)] public byte Broadcast;
        [FieldOffset(29)] public byte Readable;
        [FieldOffset(30)] public byte Writable;
        [FieldOffset(31)] public byte WriteWithoutResponse;
        [FieldOffset(32)] public byte SignedWrite;
        [FieldOffset(33)] public byte Notifiable;
        [FieldOffset(34)] public byte Indicatable;
        [FieldOffset(35)] public byte Extended;
    }

    [DllImport("BluetoothApis.dll")]
    private static extern int BluetoothGATTGetCharacteristics(SafeFileHandle device, nint service, ushort count, nint characteristics, out ushort actual, uint flags);
    [DllImport("BluetoothApis.dll")]
    private static extern int BluetoothGATTGetCharacteristicValue(SafeFileHandle device, ref GattCharacteristic characteristic, uint size, byte[] value, out ushort required, uint flags);
}

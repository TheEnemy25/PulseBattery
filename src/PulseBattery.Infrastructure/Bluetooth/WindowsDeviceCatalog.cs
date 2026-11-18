using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using PulseBattery.Infrastructure.Hid;

namespace PulseBattery.Infrastructure.Bluetooth;

public sealed record WindowsDeviceNode(string Id, string Name, string ClassName, string ContainerId, int? BatteryPercent, string? BluetoothAddress);

public sealed class WindowsDeviceCatalog
{
    private static readonly Guid DeviceProperties = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private static readonly NativeMethods.PropertyKey BatteryKey = new(new("104ea319-6ee2-4701-bd47-8ddbf425bbe5"), 2);
    private static readonly NativeMethods.PropertyKey ContainerKey = new(new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);

    public IReadOnlyList<WindowsDeviceNode> Enumerate(CancellationToken cancellationToken = default)
    {
        var set = NativeMethods.SetupDiGetClassDevsW(0, null, 0, 6); // ALLCLASSES | PRESENT
        if (set == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
        var nodes = new List<WindowsDeviceNode>();
        try
        {
            for (uint index = 0; ; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new NativeMethods.DeviceInfo { Size = Marshal.SizeOf<NativeMethods.DeviceInfo>() };
                if (!NativeMethods.SetupDiEnumDeviceInfo(set, index, ref info))
                {
                    if (Marshal.GetLastWin32Error() != 259) throw new Win32Exception(Marshal.GetLastWin32Error());
                    break;
                }
                var idBuffer = new StringBuilder(2048);
                if (!NativeMethods.SetupDiGetDeviceInstanceIdW(set, ref info, idBuffer, (uint)idBuffer.Capacity, out _)) continue;
                var id = idBuffer.ToString();
                var batteryBytes = Read(set, ref info, BatteryKey, out var batteryType);
                var battery = ParseBattery(batteryBytes, batteryType);
                if (battery is null && !id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) continue;
                var name = ReadString(set, ref info, new(DeviceProperties, 14));
                if (name.Length == 0) name = ReadString(set, ref info, new(DeviceProperties, 2));
                var className = ReadString(set, ref info, new(DeviceProperties, 9));
                var containerBytes = Read(set, ref info, ContainerKey, out _);
                var container = containerBytes.Length == 16 ? new Guid(containerBytes).ToString("D") : id;
                var match = Regex.Match(id, @"(?:DEV_|BluetoothDevice_)([0-9A-F]{12})", RegexOptions.IgnoreCase);
                nodes.Add(new(id, name, className, container, battery, match.Success ? match.Groups[1].Value.ToUpperInvariant() : null));
            }
        }
        finally { NativeMethods.SetupDiDestroyDeviceInfoList(set); }
        return nodes;
    }

    public static int? ParseBattery(byte[] bytes, uint type)
    {
        int? value = type switch
        {
            3 when bytes.Length >= 1 => bytes[0], // DEVPROP_TYPE_BYTE
            5 when bytes.Length >= 2 => BitConverter.ToUInt16(bytes),
            7 when bytes.Length >= 4 && BitConverter.ToUInt32(bytes) <= 100 => (int)BitConverter.ToUInt32(bytes),
            0x12 when int.TryParse(Encoding.Unicode.GetString(bytes).TrimEnd('\0'), out var parsed) => parsed,
            _ => null
        };
        return value is >= 0 and <= 100 ? value : null;
    }
    private static string ReadString(nint set, ref NativeMethods.DeviceInfo info, NativeMethods.PropertyKey key) => Encoding.Unicode.GetString(Read(set, ref info, key, out _)).TrimEnd('\0');
    private static byte[] Read(nint set, ref NativeMethods.DeviceInfo info, NativeMethods.PropertyKey key, out uint type)
    {
        var buffer = new byte[4096];
        if (!NativeMethods.SetupDiGetDevicePropertyW(set, ref info, in key, out type, buffer, (uint)buffer.Length, out var size, 0)) return [];
        return buffer[..(int)Math.Min(size, (uint)buffer.Length)];
    }
}

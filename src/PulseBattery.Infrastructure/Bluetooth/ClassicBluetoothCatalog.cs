using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PulseBattery.Infrastructure.Bluetooth;

public sealed record BluetoothPeer(string Address, string Name, uint ClassOfDevice, bool Connected);

public sealed class ClassicBluetoothCatalog
{
    [StructLayout(LayoutKind.Sequential)] private struct SystemTime { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DeviceInfo
    {
        public int Size;
        public ulong Address;
        public uint ClassOfDevice;
        [MarshalAs(UnmanagedType.Bool)] public bool Connected;
        [MarshalAs(UnmanagedType.Bool)] public bool Remembered;
        [MarshalAs(UnmanagedType.Bool)] public bool Authenticated;
        public SystemTime LastSeen, LastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string Name;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SearchParameters
    {
        public int Size;
        [MarshalAs(UnmanagedType.Bool)] public bool Authenticated, Remembered, Unknown, Connected, Inquiry;
        public byte TimeoutMultiplier;
        public nint Radio;
    }
    public IReadOnlyList<BluetoothPeer> Enumerate()
    {
        var search = new SearchParameters { Size = Marshal.SizeOf<SearchParameters>(), Authenticated = true, Remembered = true, Connected = true };
        var device = new DeviceInfo { Size = Marshal.SizeOf<DeviceInfo>(), Name = "" };
        var handle = BluetoothFindFirstDevice(ref search, ref device);
        if (handle == 0)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 0 or 259 or 1168) return [];
            throw new Win32Exception(error);
        }
        var peers = new List<BluetoothPeer>();
        try
        {
            do
            {
                peers.Add(new(device.Address.ToString("X12"), device.Name, device.ClassOfDevice, device.Connected));
                device = new DeviceInfo { Size = Marshal.SizeOf<DeviceInfo>(), Name = "" };
            } while (BluetoothFindNextDevice(handle, ref device));
        }
        finally { BluetoothFindDeviceClose(handle); }
        return peers;
    }
    [DllImport("bthprops.cpl", SetLastError = true)] private static extern nint BluetoothFindFirstDevice(ref SearchParameters search, ref DeviceInfo device);
    [DllImport("bthprops.cpl", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BluetoothFindNextDevice(nint handle, ref DeviceInfo device);
    [DllImport("bthprops.cpl")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BluetoothFindDeviceClose(nint handle);
}

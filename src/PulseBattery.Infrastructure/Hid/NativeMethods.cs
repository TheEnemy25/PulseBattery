using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PulseBattery.Infrastructure.Hid;

internal static partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct InterfaceData { internal int Size; internal Guid ClassGuid; internal int Flags; internal nuint Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInfo { internal int Size; internal Guid ClassGuid; internal int DevInst; internal nuint Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Attributes { internal int Size; internal ushort VendorId; internal ushort ProductId; internal ushort Version; }
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct Caps
    {
        internal ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
        internal fixed ushort Reserved[17];
        internal ushort LinkNodes, InputButtons, InputValues, InputIndices, OutputButtons, OutputValues, OutputIndices, FeatureButtons, FeatureValues, FeatureIndices;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey(Guid format, uint id) { internal Guid Format = format; internal uint Id = id; }

    [DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint SetupDiGetClassDevsW(in Guid guid, string? enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint SetupDiGetClassDevsW(nint guid, string? enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiEnumDeviceInterfaces(nint set, nint device, in Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceInterfaceDetailW(nint set, ref InterfaceData data, nint detail, uint size, out uint required, nint info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceInterfaceDetailW(nint set, ref InterfaceData data, nint detail, uint size, out uint required, ref DeviceInfo info);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiEnumDeviceInfo(nint set, uint index, ref DeviceInfo info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceInstanceIdW(nint set, ref DeviceInfo info, System.Text.StringBuilder id, uint size, out uint required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDevicePropertyW(nint set, ref DeviceInfo info, in PropertyKey key, out uint type, byte[] buffer, uint size, out uint required, uint flags);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiDestroyDeviceInfoList(nint set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetProductString(SafeFileHandle handle, byte[] buffer, uint length);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out nint data);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_FreePreparsedData(nint data);
    [DllImport("hid.dll")] internal static extern int HidP_GetCaps(nint data, out Caps caps);
    [DllImport("hid.dll")] internal static extern int HidP_GetValueCaps(int reportType, byte[] caps, ref ushort count, nint data);
    [DllImport("hid.dll")] internal static extern int HidP_GetButtonCaps(int reportType, byte[] caps, ref ushort count, nint data);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetFeature(SafeFileHandle handle, byte[] report, uint length);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetInputReport(SafeFileHandle handle, byte[] report, uint length);
    [DllImport("hid.dll")] internal static extern int HidP_GetUsageValue(int reportType, ushort page, ushort link, ushort usage, out uint value, nint data, byte[] report, uint length);
}

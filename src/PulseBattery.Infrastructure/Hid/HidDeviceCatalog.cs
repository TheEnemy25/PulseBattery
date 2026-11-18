using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using PulseBattery.Infrastructure.Bluetooth;

namespace PulseBattery.Infrastructure.Hid;

public sealed record HidDeviceInfo(string Path, ushort VendorId, ushort ProductId, string ProductName,
    ushort UsagePage, ushort Usage, ushort InputLength, ushort OutputLength, byte[] OutputReportIds,
    ushort FeatureLength = 0, IReadOnlyList<HidBatteryValue>? BatteryValues = null, string? ContainerId = null);

public sealed record HidBatteryValue(int ReportType, byte ReportId, ushort UsagePage, ushort Usage, ushort LinkCollection, int LogicalMin, int LogicalMax, int PhysicalMin, int PhysicalMax);

public sealed class HidDeviceCatalog
{
    public IReadOnlyList<HidDeviceInfo> Enumerate()
    {
        NativeMethods.HidD_GetHidGuid(out var guid);
        var set = NativeMethods.SetupDiGetClassDevsW(in guid, null, 0, 0x12);
        if (set == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
        var devices = new List<HidDeviceInfo>();
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new NativeMethods.InterfaceData { Size = Marshal.SizeOf<NativeMethods.InterfaceData>() };
                if (!NativeMethods.SetupDiEnumDeviceInterfaces(set, 0, in guid, index, ref data))
                {
                    if (Marshal.GetLastWin32Error() != 259) throw new Win32Exception(Marshal.GetLastWin32Error());
                    break;
                }
                NativeMethods.SetupDiGetDeviceInterfaceDetailW(set, ref data, 0, 0, out var size, 0);
                if (size is < 8 or > 65536) continue;
                var detail = Marshal.AllocHGlobal((int)size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    var info = new NativeMethods.DeviceInfo { Size = Marshal.SizeOf<NativeMethods.DeviceInfo>() };
                    if (!NativeMethods.SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, size, out _, ref info)) continue;
                    var node = WindowsDeviceCatalog.ReadNode(set, ref info);
                    var path = Marshal.PtrToStringUni(detail + 4)!;
                    using var handle = NativeMethods.CreateFileW(path, 0, 3, 0, 3, 0, 0);
                    if (handle.IsInvalid) continue;
                    var attributes = new NativeMethods.Attributes { Size = Marshal.SizeOf<NativeMethods.Attributes>() };
                    if (!NativeMethods.HidD_GetAttributes(handle, ref attributes)) continue;
                    if (!NativeMethods.HidD_GetPreparsedData(handle, out var parsed)) continue;
                    try
                    {
                        if (NativeMethods.HidP_GetCaps(parsed, out var caps) < 0) continue;
                        var name = new byte[512];
                        NativeMethods.HidD_GetProductString(handle, name, (uint)name.Length);
                        var reportIds = new HashSet<byte>();
                        ReadReportIds(parsed, caps.OutputValues, false, reportIds);
                        ReadReportIds(parsed, caps.OutputButtons, true, reportIds);
                        devices.Add(new(path, attributes.VendorId, attributes.ProductId,
                            string.IsNullOrWhiteSpace(Encoding.Unicode.GetString(name).TrimEnd('\0')) ? node?.Name ?? "" : Encoding.Unicode.GetString(name).TrimEnd('\0'), caps.UsagePage, caps.Usage,
                            caps.InputLength, caps.OutputLength, reportIds.ToArray(), caps.FeatureLength,
                            ReadBatteryValues(parsed, 0, caps.InputValues).Concat(ReadBatteryValues(parsed, 2, caps.FeatureValues)).ToArray(), node?.ContainerId));
                    }
                    finally { NativeMethods.HidD_FreePreparsedData(parsed); }
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { NativeMethods.SetupDiDestroyDeviceInfoList(set); }
        return devices;
    }

    private static void ReadReportIds(nint parsed, ushort count, bool buttons, HashSet<byte> result)
    {
        if (count == 0) return;
        var bytes = new byte[count * 72]; // HIDP_VALUE_CAPS and HIDP_BUTTON_CAPS are 72 bytes.
        var status = buttons ? NativeMethods.HidP_GetButtonCaps(1, bytes, ref count, parsed) : NativeMethods.HidP_GetValueCaps(1, bytes, ref count, parsed);
        if (status < 0) return;
        for (var i = 0; i < count; i++) result.Add(bytes[i * 72 + 2]);
    }
    private static IReadOnlyList<HidBatteryValue> ReadBatteryValues(nint parsed, int reportType, ushort count)
    {
        if (count == 0) return [];
        var bytes = new byte[count * 72];
        if (NativeMethods.HidP_GetValueCaps(reportType, bytes, ref count, parsed) < 0) return [];
        var values = new List<HidBatteryValue>();
        for (var i = 0; i < count; i++)
        {
            var offset = i * 72;
            var page = BitConverter.ToUInt16(bytes, offset);
            var min = BitConverter.ToUInt16(bytes, offset + 56);
            var max = bytes[offset + 12] != 0 ? BitConverter.ToUInt16(bytes, offset + 58) : min;
            foreach (var usage in page == 6 ? new ushort[] { 0x20 } : page == 0x85 ? new ushort[] { 0x64, 0x65, 0x44 } : [])
                if (usage >= min && usage <= max)
                    values.Add(new(reportType, bytes[offset + 2], page, usage, BitConverter.ToUInt16(bytes, offset + 6),
                        BitConverter.ToInt32(bytes, offset + 40), BitConverter.ToInt32(bytes, offset + 44),
                        BitConverter.ToInt32(bytes, offset + 48), BitConverter.ToInt32(bytes, offset + 52)));
        }
        return values;
    }
}

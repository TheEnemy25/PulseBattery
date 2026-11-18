using System.Text;
using PulseBattery.Core;
using PulseBattery.Infrastructure.Hid;
using PulseBattery.Infrastructure.Protocols;

namespace PulseBattery.Infrastructure.Providers;

public sealed class LogitechBatteryProvider(HidDeviceCatalog catalog, Action<string>? trace = null) : IBatteryProvider
{
    private readonly Dictionary<string, string> _names = [];
    private readonly HashSet<string> _batteryDevices = [];
    public async Task<IReadOnlyList<BatteryDevice>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        var candidates = await Task.Run(() => LogitechEndpoints.Select(catalog.Enumerate()), cancellationToken);
        var results = new List<BatteryDevice>();
        foreach (var endpoint in candidates)
        {
            var receiver = endpoint.Device;
            var id = endpoint.Id;
            var name = _names.GetValueOrDefault(id, "Logitech LIGHTSPEED");
            var batterySupported = _batteryDevices.Contains(id) || receiver.ProductId is >= 0xC500 and <= 0xC5FF;
            try
            {
                using var connection = new HidConnection(receiver);
                byte deviceIndex = 1;
                byte softwareId = 12;
                var deviceResponded = false;
                async Task<byte[]?> Query(byte feature, byte function, params byte[] parameters)
                {
                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        // A changing software ID keeps delayed ROOT replies out of subsequent feature queries.
                        softwareId = (byte)(softwareId % 15 + 1);
                        byte fn = (byte)((function << 4) | softwareId);
                        var request = new byte[20];
                        request[0] = 0x11; request[1] = deviceIndex; request[2] = feature; request[3] = fn;
                        parameters.CopyTo(request, 4);
                        await connection.WriteAsync(request, cancellationToken);
                        var response = await connection.ReadUntilAsync(r => BatteryReports.IsLogitechReply(r, deviceIndex, feature, fn) ||
                            (r.Length >= 7 && r[1] == deviceIndex && r[2] == 0xFF && r[3] == feature && r[4] == fn), TimeSpan.FromMilliseconds(850), cancellationToken);
                        trace?.Invoke($"HID++ {Convert.ToHexString(request)} => {(response is null ? "timeout" : Convert.ToHexString(response))}");
                        if (response is not null) { deviceResponded = true; return response; }
                    }
                    return null;
                }
                async Task<byte> Feature(ushort feature)
                {
                    var reply = await Query(0, 0, (byte)(feature >> 8), (byte)feature, 0);
                    return reply is { Length: >= 7 } && reply[2] != 0xFF ? reply[4] : (byte)0;
                }
                var unified = await Feature(0x1004);
                if (!deviceResponded && receiver.ProductId is not (>= 0xC500 and <= 0xC5FF))
                {
                    deviceIndex = 0xFF;
                    unified = await Feature(0x1004);
                }
                if (!deviceResponded)
                {
                    results.Add(new(id, name, DeviceKind.Mouse, null, null, ReadingState.Unavailable, "HID++", "Розбуди мишку або перевір підключення приймача.") { PhysicalId = receiver.ContainerId, BatterySupported = batterySupported });
                    continue;
                }
                var batteryFeature = unified != 0 ? unified : await Feature(0x1000);
                if (batteryFeature != 0) { _batteryDevices.Add(id); batterySupported = true; }
                var batteryReply = batteryFeature == 0 ? null : await Query(batteryFeature, unified != 0 ? (byte)1 : (byte)0);
                var battery = batteryReply is not null && batteryReply[2] != 0xFF ? BatteryReports.LogitechBattery(batteryReply, unified != 0) : (Level: (int?)null, Charging: (bool?)null);
                var nameFeature = await Feature(0x0005);
                if (nameFeature != 0)
                {
                    var lengthReply = await Query(nameFeature, 0);
                    if (lengthReply is { Length: >= 5 } && lengthReply[4] is > 0 and <= 100)
                    {
                        var length = lengthReply[4];
                        var bytes = new List<byte>();
                        while (bytes.Count < length)
                        {
                            var part = await Query(nameFeature, 1, (byte)bytes.Count);
                            if (part is null || part[2] == 0xFF) break;
                            bytes.AddRange(part.Skip(4).Take(length - bytes.Count));
                        }
                        if (bytes.Count == length) _names[id] = name = "Logitech " + Encoding.UTF8.GetString(bytes.ToArray());
                    }
                }
                var kind = DeviceClassification.FromName(name);
                if (nameFeature != 0)
                {
                    var kindReply = await Query(nameFeature, 2);
                    if (kindReply is { Length: >= 5 } && kindReply[2] != 0xFF)
                        kind = kindReply[4] switch { 0 or 2 => DeviceKind.Keyboard, 3 or 4 or 5 => DeviceKind.Mouse, _ => kind };
                }
                results.Add(new(id, name, kind, battery.Level, battery.Charging,
                    battery.Level is null ? ReadingState.Unavailable : ReadingState.Available, "HID++", battery.Level is null ? "Розбуди мишку або перевір підключення приймача." : receiver.ProductId == 0xC09B ? "USB-кабель" : "LIGHTSPEED · USB-приймач") { PhysicalId = receiver.ContainerId, BatterySupported = batterySupported });
            }
            catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception)
            {
                results.Add(new(id, name, DeviceKind.Mouse, null, null, ReadingState.Unavailable, "HID++", exception.Message) { PhysicalId = receiver.ContainerId, BatterySupported = batterySupported });
            }
        }
        return results;
    }
}

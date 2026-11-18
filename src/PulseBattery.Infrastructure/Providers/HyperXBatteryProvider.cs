using PulseBattery.Core;
using PulseBattery.Infrastructure.Hid;
using PulseBattery.Infrastructure.Protocols;

namespace PulseBattery.Infrastructure.Providers;

public sealed class HyperXBatteryProvider(HidDeviceCatalog catalog) : IBatteryProvider
{
    public async Task<IReadOnlyList<BatteryDevice>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        var interfaces = await Task.Run(() => catalog.Enumerate().Where(d => d.VendorId == 0x03F0 &&
            d.ProductId is 0x06BE or 0x02CC && (d.UsagePage >= 0xFF00 || d.OutputReportIds.Contains((byte)0x0C))).ToArray(), cancellationToken);
        var results = new List<BatteryDevice>();
        foreach (var group in interfaces.GroupBy(d => d.Path.Split('#')[2][..d.Path.Split('#')[2].LastIndexOf('&')]))
        {
            var connections = new List<HidConnection>();
            try
            {
                foreach (var item in group)
                {
                    try { connections.Add(new(item)); }
                    catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception) { }
                }
                var writer = connections.FirstOrDefault(c => c.Device.OutputReportIds.Contains((byte)0x0C) && c.Device.OutputLength >= 64);
                int? level = null;
                bool? charging = null;
                if (writer is not null)
                {
                    async Task<int?> Query(byte command)
                    {
                        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        var reads = connections.Where(c => c.Device.InputLength > 0).Select(c => c.ReadUntilAsync(
                            r => BatteryReports.HyperXLevel(r, command) is not null, TimeSpan.FromMilliseconds(1500), readCancellation.Token)).ToList();
                        try
                        {
                            byte[] request = new byte[64];
                            request[0] = 0x0C; request[1] = 2; request[2] = 3; request[3] = 1; request[5] = command;
                            await writer.WriteAsync(request, cancellationToken);
                            while (reads.Count > 0)
                            {
                                var completed = await Task.WhenAny(reads);
                                reads.Remove(completed);
                                var response = await completed;
                                if (response is not null) return BatteryReports.HyperXLevel(response, command);
                            }
                            return null;
                        }
                        finally
                        {
                            await readCancellation.CancelAsync();
                            try { await Task.WhenAll(reads); }
                            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                        }
                    }
                    level = await Query(0x06);
                    var chargeValue = await Query(0x48);
                    charging = chargeValue is 0 or 1 ? chargeValue == 1 : null;
                }
                results.Add(new($"hyperx:{group.Key}", "HyperX Cloud III S Wireless", DeviceKind.Headset, level, charging,
                    level is null ? ReadingState.Unavailable : ReadingState.Available, "HyperX HID", level is null ? "Увімкни навушники. Приймач знайдено, відповіді про заряд поки немає." : "2.4 ГГц · USB-приймач") { PhysicalId = group.First().ContainerId, BatterySupported = true });
            }
            finally { foreach (var connection in connections) connection.Dispose(); }
        }
        return results;
    }
}

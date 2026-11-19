using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PulseBattery.Core;
using PulseBattery.Infrastructure.Hid;
using PulseBattery.Infrastructure.Protocols;

namespace PulseBattery.Infrastructure.Providers;

// The GPL device backend is a separate executable communicating over a local JSON stream.
public sealed class VendorBatteryProvider(HidDeviceCatalog catalog, Action<string>? trace = null) : IBatteryProvider, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly VendorReadingCache _readings = new();
    private Process? _process;
    private EventHandler? _exitHandler;

    public async Task<IReadOnlyList<BatteryDevice>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_process is null || _process.HasExited) await Task.Run(Start, cancellationToken);
            await _process!.StandardInput.WriteLineAsync("scan".AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
            var line = await _process.StandardOutput.ReadLineAsync(cancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            if (line is null) throw new IOException("The battery device backend stopped unexpectedly.");
            trace?.Invoke("Vendor battery scan received.");
            var interfaces = await Task.Run(catalog.Enumerate, cancellationToken);
            var observations = new List<VendorReading>();
            ParseSnapshot(line, interfaces, observations.Add);
            using var snapshot = JsonDocument.Parse(line);
            var disconnected = snapshot.RootElement.GetProperty("devices").EnumerateArray()
                .Where(d => d.TryGetProperty("connected", out var connected) && connected.ValueKind == JsonValueKind.False)
                .Select(d => d.GetProperty("key").GetString() ?? "").ToHashSet(StringComparer.Ordinal);
            var present = interfaces.Select(h => h.ContainerId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            return _readings.Update(observations, present, disconnected, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) { Stop(); throw; }
        catch { Stop(); throw; }
        finally { _gate.Release(); }
    }

    private void Start()
    {
        Stop();
        var folder = ExtractBackend();
        var start = new ProcessStartInfo(Path.Combine(folder, "PulseBattery.DeviceBackend.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false)
        };
        start.ArgumentList.Add("--parent"); start.ArgumentList.Add(Environment.ProcessId.ToString());
        _process = Process.Start(start) ?? throw new IOException("The battery device backend could not be started.");
        _process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) trace?.Invoke("Device backend: " + e.Data); };
        _process.BeginErrorReadLine();
        if (_exitHandler is null) { _exitHandler = (_, _) => Stop(); AppDomain.CurrentDomain.ProcessExit += _exitHandler; }
    }

    public static string ExtractBackend(string? cacheRoot = null)
    {
        using var resource = typeof(VendorBatteryProvider).Assembly.GetManifestResourceStream("PulseBattery.DeviceBackend.zip")
            ?? throw new IOException("The battery device backend is missing from this build.");
        var hash = Convert.ToHexString(SHA256.HashData(resource));
        resource.Position = 0;
        var folder = Path.Combine(cacheRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PulseBattery", "DeviceBackend"), hash[..20]);
        Directory.CreateDirectory(folder);
        using var archive = new ZipArchive(resource, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            if (entry.Name != entry.FullName || entry.Name.Length == 0) throw new InvalidDataException("Unexpected backend package entry.");
            var path = Path.Combine(folder, entry.Name);
            using var content = entry.Open();
            var expected = SHA256.HashData(content);
            if (File.Exists(path))
            {
                using var existing = File.OpenRead(path);
                if (SHA256.HashData(existing).AsSpan().SequenceEqual(expected)) continue;
            }
            // Validate cached executables against the embedded package before starting them.
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var input = entry.Open())
                using (var output = File.Create(temporary)) input.CopyTo(output);
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return folder;
    }

    public static IReadOnlyList<BatteryDevice> ParseSnapshot(string json, IReadOnlyList<HidDeviceInfo> interfaces) => ParseSnapshot(json, interfaces, null);

    private static IReadOnlyList<BatteryDevice> ParseSnapshot(string json, IReadOnlyList<HidDeviceInfo> interfaces, Action<VendorReading>? observe)
    {
        if (json.Length > 1024 * 1024) throw new InvalidDataException("The battery device response is too large.");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("schema", out var schema) || schema.GetInt32() != 1 ||
            !root.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Unsupported battery device response.");
        if (root.TryGetProperty("error", out _)) throw new IOException("The battery device driver scan failed.");
        static string Text(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        static int Number(JsonElement item, string key, int fallback = 0) => item.TryGetProperty(key, out var value) && value.TryGetInt32(out var number) ? number : fallback;
        static bool Flag(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
        var readings = new List<BatteryDevice>();
        foreach (var item in devices.EnumerateArray())
        {
            var key = Text(item, "key"); var name = Text(item, "name");
            if (key.Length == 0 || name.Length == 0 || !Flag(item, "connected")) continue;
            // Never allow a backend phone, microphone or arbitrary USB record into Pulse's peripheral list.
            var kind = Text(item, "kind") switch
            {
                "Mouse" => DeviceKind.Mouse, "Keyboard" => DeviceKind.Keyboard,
                "Headset" => DeviceClassification.FromName(name) is DeviceKind.Earbuds or DeviceKind.Headphones ? DeviceClassification.FromName(name) : DeviceKind.Headset,
                "Gamepad" => DeviceKind.Gamepad, "Other" => DeviceKind.Other, _ => (DeviceKind?)null
            };
            if (kind is null) continue;
            var vid = Number(item, "vid"); var pid = Number(item, "pid");
            var exact = interfaces.FirstOrDefault(h => h.Path.Equals(Text(item, "path"), StringComparison.OrdinalIgnoreCase));
            var candidates = interfaces.Where(h => h.VendorId == vid && h.ProductId == pid).ToArray();
            var containers = candidates.Select(h => h.ContainerId).Where(c => c is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var physical = exact?.ContainerId ?? (containers.Length == 1 ? containers[0] : null);
            if (kind == DeviceKind.Other)
            {
                var roles = candidates.Select(h => DeviceClassification.FromHid(h.ProductName, h.UsagePage, h.Usage))
                    .Where(k => k != DeviceKind.Other).Distinct().ToArray();
                kind = DeviceClassification.FromName(name);
                if (kind == DeviceKind.Other)
                    kind = roles.Contains(DeviceKind.Mouse) ? DeviceKind.Mouse : roles.Contains(DeviceKind.Gamepad) ? DeviceKind.Gamepad :
                        roles.Contains(DeviceKind.Keyboard) ? DeviceKind.Keyboard : roles.FirstOrDefault(DeviceKind.Other);
                if (kind == DeviceKind.Other) continue;
            }
            var percent = Number(item, "percent", -1);
            var coarse = Text(item, "level").ToLowerInvariant() switch
            {
                "empty" => BatteryChargeLevel.Empty, "low" or "critical" => BatteryChargeLevel.Low,
                "medium" or "good" => BatteryChargeLevel.Medium, "full" => BatteryChargeLevel.Full, _ => (BatteryChargeLevel?)null
            };
            if (coarse is not null) percent = -1; // Upstream stores approximate display values for XInput levels.
            var source = key.StartsWith("logi:", StringComparison.Ordinal) ? "HID++" : key.StartsWith("hyperx:", StringComparison.Ordinal) ? "HyperX HID" :
                key.StartsWith("xinput:", StringComparison.Ordinal) ? "XInput battery" : "Wireless Battery";
            var id = "vendor-battery:" + key;
            if (source == "XInput battery" && kind == DeviceKind.Gamepad && key.Split(':') is [_, var slot, _]) id = "xinput:" + slot;
            // Retain existing preferences for the original directly supported mouse/headset.
            if (vid == 0x046D && physical is not null && !readings.Any(r => r.PhysicalId == physical && r.Kind == kind))
            {
                var endpoints = LogitechEndpoints.Select(interfaces).Where(e => e.Device.ContainerId == physical).ToArray();
                if (endpoints.Length == 1) id = endpoints[0].Id;
            }
            if (vid == 0x03F0 && pid is 0x06BE or 0x02CC &&
                candidates.FirstOrDefault(h => h.UsagePage >= 0xFF00 || h.OutputReportIds.Contains((byte)0x0C)) is { } hyperx)
            {
                var parts = hyperx.Path.Split('#');
                if (parts.Length > 2 && parts[2].LastIndexOf('&') is > 0 and var end) id = "hyperx:" + parts[2][..end];
            }
            var available = !Flag(item, "asleep") && (percent is >= 0 and <= 100 || coarse is not null);
            var detail = string.Join(" · ", new[] { Text(item, "via"), Text(item, "detail") }.Where(s => s.Length > 0));
            readings.Add(new(id, name, kind.Value, percent is >= 0 and <= 100 ? percent : null,
                available ? Flag(item, "charging") : null, available ? ReadingState.Available : ReadingState.Unavailable, source, detail)
                { PhysicalId = physical, BatterySupported = true, ChargeLevel = coarse });
            observe?.Invoke(new(readings[^1], key, Number(item, "graceSeconds")));
        }
        return readings;
    }

    private void Stop()
    {
        var process = _process; _process = null;
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }
    public void Dispose()
    {
        Stop();
        if (_exitHandler is not null) { AppDomain.CurrentDomain.ProcessExit -= _exitHandler; _exitHandler = null; }
    }
}

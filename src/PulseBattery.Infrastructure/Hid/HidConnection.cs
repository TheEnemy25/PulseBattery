using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PulseBattery.Infrastructure.Hid;

internal sealed class HidConnection : IDisposable
{
    private readonly FileStream _stream;
    public HidDeviceInfo Device { get; }
    public HidConnection(HidDeviceInfo device)
    {
        Device = device;
        var handle = NativeMethods.CreateFileW(device.Path, 0xC0000000, 3, 0, 3, 0x40000000, 0);
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        try { _stream = new FileStream(handle, FileAccess.ReadWrite, 1, true); }
        catch { handle.Dispose(); throw; }
    }

    public async Task WriteAsync(byte[] report, CancellationToken cancellationToken)
    {
        if (report.Length > Device.OutputLength) throw new IOException("Report exceeds HID output length.");
        var padded = new byte[Device.OutputLength];
        report.CopyTo(padded, 0);
        await _stream.WriteAsync(padded, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    public async Task<byte[]?> ReadUntilAsync(Func<byte[], bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var buffer = new byte[Math.Max(64, (int)Device.InputLength)];
                var length = await _stream.ReadAsync(buffer, deadline.Token);
                if (length == 0) return null;
                Array.Resize(ref buffer, length);
                if (predicate(buffer)) return buffer;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
    }

    public void Dispose() => _stream.Dispose();
}

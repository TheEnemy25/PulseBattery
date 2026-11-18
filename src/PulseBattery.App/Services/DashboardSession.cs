using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Threading;
using PulseBattery.App.ViewModels;

namespace PulseBattery.App.Services;

// The resident monitor owns hardware and settings. The temporary UI process only
// receives snapshots and sends commands; it never competes for a HID connection.
internal sealed class DashboardSession : IDisposable
{
    private readonly DashboardViewModel _model;
    private readonly Dispatcher _dispatcher;
    private readonly Action<DashboardSession> _closed;
    private readonly Action _exit;
    private readonly CancellationTokenSource _lifetime;
    private readonly NamedPipeServerStream _pipe;
    private readonly Channel<DashboardPacket> _outgoing = Channel.CreateUnbounded<DashboardPacket>();
    private readonly Process _process;
    private long _acknowledged;
    private bool _disposed;
    private volatile bool _ready;
    public int ProcessId => _process.Id;
    public bool IsReady => _ready;
    public bool IsRunning => !_disposed && !_process.HasExited;

    public DashboardSession(DashboardViewModel model, Dispatcher dispatcher, CancellationToken lifetime,
        Action<DashboardSession> closed, Action exit)
    {
        _model = model; _dispatcher = dispatcher; _closed = closed; _exit = exit;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var name = $"PulseBattery.Dashboard.{Environment.ProcessId}.{Guid.NewGuid():N}";
        _pipe = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            _process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--dashboard-session {name}")
                { UseShellExecute = false, CreateNoWindow = true }) ?? throw new InvalidOperationException("Could not open Pulse.");
        }
        catch { _pipe.Dispose(); _lifetime.Dispose(); throw; }
        _model.SettingsChanged += SendState;
        _model.DevicesChanged += SendState;
        _model.PropertyChanged += ModelChanged;
        _ = CommunicateAsync();
    }
    public void Activate() => _outgoing.Writer.TryWrite(new() { Type = "activate" });
    public void CloseWindow() => _outgoing.Writer.TryWrite(new() { Type = "close" });
    private void ModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(DashboardViewModel.IsBusy) or nameof(DashboardViewModel.Error)) SendState();
    }
    private void SendState()
    {
        if (_disposed) return;
        _outgoing.Writer.TryWrite(new()
        {
            Type = "state", Settings = _model.Settings, Devices = _model.Devices.Select(device => device.Device).ToArray(),
            Busy = _model.IsBusy, Error = _model.Error, LastRefresh = _model.LastRefreshAt, Acknowledged = _acknowledged
        });
    }
    private async Task CommunicateAsync()
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await _pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(SendState);
            using var reader = new StreamReader(_pipe, leaveOpen: true);
            using var writer = new StreamWriter(_pipe, leaveOpen: true) { AutoFlush = true };
            var sending = SendAsync(writer);
            try
            {
                while (await reader.ReadLineAsync(_lifetime.Token).ConfigureAwait(false) is { } line)
                {
                    var packet = JsonSerializer.Deserialize(line, DashboardJsonContext.Default.DashboardPacket);
                    if (packet is null) continue;
                    await _dispatcher.InvokeAsync(() =>
                    {
                        if (_disposed) return;
                        switch (packet.Type)
                        {
                            case "settings" when packet.Settings is not null:
                                _model.ApplyDashboardSettings(packet.Settings);
                                _acknowledged = packet.Revision; SendState(); break;
                            case "refresh": _ = _model.RefreshAsync(); break;
                            case "ready": _ready = true; break;
                            case "exit": _exit(); break;
                        }
                    });
                }
            }
            finally
            {
                _outgoing.Writer.TryComplete();
                try { await sending.ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or JsonException) { }
        finally
        {
            if (!_dispatcher.HasShutdownStarted) await _dispatcher.InvokeAsync(() => { Dispose(); _closed(this); });
        }
    }
    private async Task SendAsync(StreamWriter writer)
    {
        await foreach (var packet in _outgoing.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            await writer.WriteLineAsync(JsonSerializer.Serialize(packet, DashboardJsonContext.Default.DashboardPacket).AsMemory(), _lifetime.Token).ConfigureAwait(false);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _model.SettingsChanged -= SendState; _model.DevicesChanged -= SendState; _model.PropertyChanged -= ModelChanged;
        _outgoing.Writer.TryComplete(); _lifetime.Cancel(); _pipe.Dispose();
        try { if (!_process.HasExited) _process.Kill(); }
        catch (InvalidOperationException) { }
        _process.Dispose(); _lifetime.Dispose();
    }
}

using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Threading;
using PulseBattery.App.ViewModels;
using PulseBattery.App.Views;
using PulseBattery.Core;

namespace PulseBattery.App.Services;

internal sealed class DashboardClient
{
    private readonly Channel<DashboardPacket> _requests = Channel.CreateUnbounded<DashboardPacket>();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Dashboard? _window;
    public void ExitApplication()
    {
        _requests.Writer.TryWrite(new() { Type = "exit" });
        _window?.Close();
    }
    public async Task RunAsync(string name, CancellationToken lifetime)
    {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await pipe.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        DashboardPacket? initial = null;
        // A second click can enqueue activation before the connection is ready.
        while (initial?.Settings is null)
        {
            var line = await reader.ReadLineAsync(timeout.Token);
            if (line is null) return;
            var packet = JsonSerializer.Deserialize(line, DashboardJsonContext.Default.DashboardPacket);
            if (packet?.Type == "close") return;
            if (packet?.Type == "state") initial = packet;
        }
        var store = new RemoteSettingsStore(initial.Settings, _requests.Writer);
        var model = new DashboardViewModel(new BatteryMonitor([]), store, stop.Token, requestRefresh: () =>
        {
            _requests.Writer.TryWrite(new() { Type = "refresh" });
            return Task.CompletedTask;
        });
        model.ApplyDashboardState(initial.Settings, initial.Devices ?? [], initial.Busy, initial.Error, initial.LastRefresh);
        _window = new Dashboard(model);
        _window.Closed += (_, _) => _closed.TrySetResult();
        Application.Current.MainWindow = _window;
        var sending = SendAsync(writer, stop.Token);
        var receiving = ReceiveAsync(reader, store, model, stop.Token);
        _window.Show(); _window.Activate();
        _requests.Writer.TryWrite(new() { Type = "ready" });
        await _closed.Task;
        _requests.Writer.TryComplete();
        try { await sending; }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
        stop.Cancel(); pipe.Dispose();
        try { await receiving; }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
    private async Task SendAsync(StreamWriter writer, CancellationToken token)
    {
        await foreach (var packet in _requests.Reader.ReadAllAsync(token).ConfigureAwait(false))
            await writer.WriteLineAsync(JsonSerializer.Serialize(packet, DashboardJsonContext.Default.DashboardPacket).AsMemory(), token).ConfigureAwait(false);
    }
    private async Task ReceiveAsync(StreamReader reader, RemoteSettingsStore store, DashboardViewModel model, CancellationToken token)
    {
        try
        {
            while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                var packet = JsonSerializer.Deserialize(line, DashboardJsonContext.Default.DashboardPacket);
                if (packet is null) continue;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    switch (packet.Type)
                    {
                        case "activate":
                            _window?.Show();
                            if (_window is not null) { _window.WindowState = WindowState.Normal; _window.Activate(); }
                            break;
                        case "close": _window?.Close(); break;
                        case "state" when packet.Settings is not null:
                            store.Accept(packet);
                            model.ApplyDashboardState(store.Load(), packet.Devices ?? [], packet.Busy, packet.Error, packet.LastRefresh);
                            break;
                    }
                });
            }
        }
        finally
        {
            if (!Application.Current.Dispatcher.HasShutdownStarted)
                await Application.Current.Dispatcher.InvokeAsync(() => _window?.Close());
        }
    }
    private sealed class RemoteSettingsStore(AppSettings settings, ChannelWriter<DashboardPacket> requests) : ISettingsStore
    {
        private AppSettings _settings = settings;
        private long _revision;
        public AppSettings Load() => _settings;
        public void Save(AppSettings updated)
        {
            var packet = new DashboardPacket { Type = "settings", Settings = updated.Validate(), Revision = ++_revision };
            if (!requests.TryWrite(packet)) throw new IOException("Pulse monitor is no longer connected.");
            _settings = packet.Settings!;
        }
        public void Accept(DashboardPacket packet)
        {
            // An older acknowledgement must not pull a slider back while it is dragged.
            if (packet.Acknowledged >= _revision && packet.Settings is not null) _settings = packet.Settings;
        }
    }
}

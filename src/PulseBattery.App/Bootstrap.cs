using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PulseBattery.App.Services;
using PulseBattery.App.ViewModels;
using PulseBattery.App.Views;
using PulseBattery.Core;
using PulseBattery.Infrastructure;
using PulseBattery.Infrastructure.Hid;
using PulseBattery.Infrastructure.Providers;
using PulseBattery.App.Localization;

namespace PulseBattery.App;

public partial class App : Application
{
    private readonly CancellationTokenSource _lifetime = new();
    private Mutex? _instance;
    private EventWaitHandle? _activation;
    private RegisteredWaitHandle? _activationWait;
    private DashboardSession? _dashboardSession;
    private DashboardClient? _dashboardClient;
    private HwndSource? _messageSource;
    private TaskbarPanelService? _taskbar;
    private DashboardViewModel? _viewModel;
    private TrayService? _tray;
    private DispatcherTimer? _timer;
    private DispatcherTimer? _deviceDebounce;
    public bool IsExiting { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // The dashboard is a small, mostly static UI. Avoid retaining a GPU rendering
        // device and its surfaces after returning to background monitoring.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        try
        {
            if (e.Args.Contains("--check-motion"))
            {
                var folder = e.Args.SkipWhile(a => a != "--check-motion").Skip(1).FirstOrDefault() ?? "artifacts/motion-checks";
                var valid = await TaskbarMotionChecks.RunAsync(folder);
                IsExiting = true; Shutdown(valid ? 0 : 1); return;
            }
            if (e.Args.Contains("--dashboard-session"))
            {
                var pipe = e.Args.SkipWhile(a => a != "--dashboard-session").Skip(1).First();
                _dashboardClient = new();
                await _dashboardClient.RunAsync(pipe, _lifetime.Token);
                IsExiting = true; Shutdown(); return;
            }
            if (e.Args.Contains("--check-presentation"))
            {
                var folder = e.Args.SkipWhile(a => a != "--check-presentation").Skip(1).FirstOrDefault() ?? "artifacts/presentation-checks";
                var valid = await PresentationChecks.RunAsync(folder);
                IsExiting = true; Shutdown(valid ? 0 : 1); return;
            }
            var smokeTest = e.Args.Contains("--smoke-test");
            var verification = e.Args.Any(a => a is "--smoke-test" or "--check-taskbar" or "--check-startup" or "--check-menu" or "--check-background");
            if (!smokeTest && !e.Args.Contains("--check-background"))
            {
                _instance = new Mutex(true, "Local\\PulseBattery.Instance", out var firstInstance);
                _activation = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\PulseBattery.Activate");
                if (!firstInstance) { if (!e.Args.Contains("--autostart")) _activation.Set(); Shutdown(); return; }
                _activationWait = ThreadPool.RegisterWaitForSingleObject(_activation, (_, _) => Dispatcher.BeginInvoke(ShowDashboard), null, Timeout.Infinite, false);
            }
            var monitor = BatteryMonitorFactory.CreateDefault();
            var settingsStore = new JsonSettingsStore();
            var startup = verification ? null : new WindowsStartupRegistration(Environment.ProcessPath!);
            startup?.ApplyInstallerPreference(settingsStore);
            _viewModel = new(monitor, settingsStore, _lifetime.Token, startup);
            if (e.Args.Contains("--autostart") && _viewModel.Settings.StartWithWindows != true) { IsExiting = true; Shutdown(); return; }
            if (smokeTest)
            {
                var dashboard = new Dashboard(_viewModel);
                await _viewModel.RefreshAsync();
                var folder = e.Args.SkipWhile(a => a != "--smoke-test").Skip(1).FirstOrDefault() ?? "artifacts";
                Directory.CreateDirectory(folder);
                RenderPreview(dashboard, Path.Combine(folder, "preview.png"));
                using (var trayCheck = new TrayService(_viewModel, () => { })) trayCheck.Update();
                _viewModel.SettingsOpen = true;
                RenderPreview(dashboard, Path.Combine(folder, "settings-preview.png"));
                using var widgetContent = new TaskbarPanel(_viewModel, () => { });
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                widgetContent.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var widgetSize = widgetContent.DesiredSize;
                widgetContent.Arrange(new Rect(widgetSize)); widgetContent.UpdateLayout();
                var widgetBitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(widgetSize.Width)), Math.Max(1, (int)Math.Ceiling(widgetSize.Height)), 96, 96, PixelFormats.Pbgra32);
                widgetBitmap.Render(widgetContent);
                var widgetEncoder = new PngBitmapEncoder(); widgetEncoder.Frames.Add(BitmapFrame.Create(widgetBitmap));
                using (var widgetStream = File.Create(Path.Combine(folder, "panel-preview.png"))) widgetEncoder.Save(widgetStream);
                File.WriteAllText(Path.Combine(folder, "hardware.json"), JsonSerializer.Serialize(new { Devices = _viewModel.Devices.Select(d => d.Device), Error = _viewModel.Error }, new JsonSerializerOptions { WriteIndented = true }));
                IsExiting = true;
                Shutdown(_viewModel.HasError ? 1 : 0);
                return;
            }
            _tray = new(_viewModel, ShowDashboard);
            _taskbar = new(_viewModel, ShowDashboard);
            _viewModel.DevicesChanged += () => { _tray.Update(); _taskbar.Update(); };
            _viewModel.SettingsChanged += ApplySettings;
            _timer = new DispatcherTimer();
            _timer.Tick += async (_, _) => await _viewModel.RefreshAsync();
            _deviceDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _deviceDebounce.Tick += async (_, _) => { _deviceDebounce.Stop(); await _viewModel.RefreshAsync(); };
            // A hidden native window receives device broadcasts without constructing the dashboard.
            // HWND_MESSAGE windows do not receive these broadcasts, so keep a hidden top-level HWND.
            _messageSource = new HwndSource(new HwndSourceParameters("Pulse.DeviceEvents")
            {
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
                Width = 0, Height = 0, HwndSourceHook = WindowMessage
            });
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            ApplySettings();
            if (!verification && !e.Args.Contains("--autostart") && _viewModel.NeedsStartupChoice)
                new StartupPrompt(_viewModel).ShowDialog();
            if (e.Args.Contains("--dashboard")) ShowDashboard();
            await _viewModel.RefreshAsync();
            if (e.Args.Contains("--check-background"))
            {
                var folder = e.Args.SkipWhile(a => a != "--check-background").Skip(1).FirstOrDefault() ?? "artifacts/background-checks";
                var valid = await CheckBackgroundAsync(folder);
                IsExiting = true; Shutdown(valid ? 0 : 1); return;
            }
            if (!verification && !e.Args.Contains("--autostart")) _viewModel.UpdateStartupExecutable();
            if (e.Args.Contains("--check-menu"))
            {
                var folder = e.Args.SkipWhile(a => a != "--check-menu").Skip(1).FirstOrDefault() ?? "artifacts/menu-checks";
                var valid = await _taskbar.WriteMenuVerificationAsync(folder);
                IsExiting = true; Shutdown(valid ? 0 : 1); return;
            }
            if (e.Args.Contains("--check-taskbar"))
            {
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                await _taskbar.WaitForSpaceAsync();
                var folder = e.Args.SkipWhile(a => a != "--check-taskbar").Skip(1).FirstOrDefault() ?? "artifacts/validation";
                var valid = await _taskbar.WriteVerificationAsync(folder, _tray.IsVisible);
                IsExiting = true; Shutdown(valid ? 0 : 1);
                return;
            }
            if (e.Args.Contains("--check-startup")) ExitApplication();
        }
        catch (Exception exception)
        {
            var log = Path.Combine(AppContext.BaseDirectory, "startup-error.log");
            try { File.WriteAllText(log, exception.ToString()); }
            catch (Exception loggingException) when (loggingException is IOException or UnauthorizedAccessException) { }
            if (!e.Args.Any(a => a is "--check-motion" or "--dashboard-session" or "--smoke-test" or "--check-presentation" or "--check-taskbar" or "--check-startup" or "--check-menu" or "--check-background")) MessageBox.Show(exception.Message, Strings.Current["StartupError"], MessageBoxButton.OK, MessageBoxImage.Error);
            IsExiting = true;
            Shutdown(1);
        }
    }

    private void ApplySettings()
    {
        if (_viewModel is null) return;
        if (_timer is not null) { _timer.Interval = TimeSpan.FromSeconds(_viewModel.RefreshSeconds); _timer.Start(); }
        _taskbar?.SetEnabled(_viewModel.TaskbarPanel);
        _tray?.Update();
    }

    public void ShowDashboard()
    {
        if (_viewModel is null || IsExiting) return;
        if (_dashboardSession?.IsRunning == true) { _dashboardSession.Activate(); return; }
        _dashboardSession?.Dispose();
        _dashboardSession = new(_viewModel, Dispatcher, _lifetime.Token,
            session => { if (ReferenceEquals(_dashboardSession, session)) _dashboardSession = null; }, ExitApplication);
    }
    public void ExitApplication()
    {
        if (_dashboardClient is not null) { _dashboardClient.ExitApplication(); return; }
        IsExiting = true; Shutdown();
    }
    private nint WindowMessage(nint handle, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0219 && _deviceDebounce is not null) { _deviceDebounce.Stop(); _deviceDebounce.Start(); }
        return 0;
    }
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Dispatcher.BeginInvoke(async () => { if (_viewModel is not null) await _viewModel.RefreshAsync(); });
    }
    private static void RenderPreview(Dashboard dashboard, string path)
    {
        var content = (FrameworkElement)dashboard.Content;
        content.Measure(new Size(960, 690)); content.Arrange(new Rect(0, 0, 960, 690)); content.UpdateLayout();
        var target = new RenderTargetBitmap(960, 690, 96, 96, PixelFormats.Pbgra32);
        // A detached content tree needs its window background drawn explicitly.
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen()) dc.DrawRectangle(dashboard.Background, null, new Rect(0, 0, 960, 690));
        target.Render(drawing);
        target.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        _lifetime.Cancel(); _timer?.Stop(); _deviceDebounce?.Stop();
        _dashboardSession?.Dispose();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _activationWait?.Unregister(null); _activation?.Dispose();
        _messageSource?.Dispose(); _taskbar?.Dispose(); _tray?.Dispose(); _instance?.Dispose();
        base.OnExit(e);
    }
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e) { IsExiting = true; base.OnSessionEnding(e); }
}

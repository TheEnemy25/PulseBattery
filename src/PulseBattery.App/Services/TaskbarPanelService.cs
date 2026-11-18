using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PulseBattery.App.ViewModels;
using PulseBattery.App.Views;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PulseBattery.App.Localization;
using System.Windows.Automation;
using PulseBattery.Core;

namespace PulseBattery.App.Services;

public sealed class TaskbarPanelService : IDisposable
{
    private readonly DashboardViewModel _viewModel;
    private readonly Action _open;
    private readonly DispatcherTimer _timer;
    private HwndSource? _source;
    private TaskbarPanel? _panel;
    private nint _taskbar;
    private bool _enabled;
    private readonly TaskbarSpaceMonitor _space;
    private bool _disposed;
    private int _lastScalePercent;
    private int _lastSpacing;
    private int _lastCompactScalePercent, _lastCompactSpacing, _lastLayoutMode;
    private string _displayReason = "Starting";
    private TaskbarPlacement _placement;
    private TaskbarSpaceSnapshot? _verificationSpace;
    private bool _recoveringFromFullTaskbar;
    private long _recoveryFitStarted;
    private readonly WinEventProc _winEvent;
    private nint _structureHook, _locationHook;
    public bool IsAvailable { get; private set; }
    internal nint WindowHandle => _source is { IsDisposed: false } ? _source.Handle : 0;
    public event Action<bool>? AvailabilityChanged;
    private bool _layoutDirty = true;
    private DispatcherOperation? _pendingLayout;
    private int _lastHeight;
    private double _lastScale;
    private int _horizontalWidth, _stackedWidth;
    private TaskbarDisplayMode _mode = TaskbarDisplayMode.Hidden;
    private static readonly Brush HitBackground = CreateHitBackground();
    public TaskbarPanelService(DashboardViewModel viewModel, Action open)
    {
        _viewModel = viewModel; _open = open;
        _winEvent = TaskbarChanged;
        _space = new(() =>
        {
            if (!_disposed && !Application.Current.Dispatcher.HasShutdownStarted)
                Application.Current.Dispatcher.BeginInvoke(() => { if (!_disposed) Update(); });
        });
        _viewModel.DevicesChanged += InvalidateLayout;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Update();
        _timer.Start();
    }
    public void SetEnabled(bool enabled)
    {
        if (_enabled != enabled || _lastScalePercent != _viewModel.TaskbarScalePercent || _lastSpacing != _viewModel.TaskbarSpacing ||
            _lastCompactScalePercent != _viewModel.TaskbarCompactScalePercent || _lastCompactSpacing != _viewModel.TaskbarCompactSpacing ||
            _lastLayoutMode != _viewModel.TaskbarLayoutMode) InvalidateLayout();
        _enabled = enabled; _lastScalePercent = _viewModel.TaskbarScalePercent; _lastSpacing = _viewModel.TaskbarSpacing;
        _lastCompactScalePercent = _viewModel.TaskbarCompactScalePercent; _lastCompactSpacing = _viewModel.TaskbarCompactSpacing;
        _lastLayoutMode = _viewModel.TaskbarLayoutMode;
        if (!enabled) Hide("DisabledOrEmpty");
    }

    public void Update()
    {
        if (_disposed) return;
        var taskbar = FindWindowW("Shell_TrayWnd", null);
        if (!_enabled || _viewModel.TaskbarDevices.Count == 0) { _space.Watch(0); Hide("DisabledOrEmpty"); return; }
        _space.Watch(taskbar);
        var tray = taskbar == 0 ? 0 : FindWindowExW(taskbar, 0, "TrayNotifyWnd", null);
        if (taskbar == 0 || tray == 0 || !IsWindowVisible(taskbar)) { Hide("TaskbarUnavailable"); return; }
        if (!GetWindowRect(taskbar, out var bar) || !GetWindowRect(tray, out var notification) || bar.Right - bar.Left < bar.Bottom - bar.Top) { Hide("UnsupportedTaskbar"); return; }
        if (_source is null || _source.IsDisposed || !IsWindow(_source.Handle) || _taskbar != taskbar)
        {
            ReleaseHost();
            _taskbar = taskbar;
            GetWindowThreadProcessId(taskbar, out var explorer);
            _structureHook = SetWinEventHook(0x8000, 0x8003, 0, _winEvent, 0, 0, 2);
            _locationHook = SetWinEventHook(0x800B, 0x800B, 0, _winEvent, explorer, 0, 2);
            _panel = new(_viewModel, _open);
            // A layered child belongs to the taskbar itself and is not part of Show Desktop.
            var previousDpi = SetThreadDpiAwarenessContext(GetWindowDpiAwarenessContext(taskbar));
            try
            {
                _source = new HwndSource(new HwndSourceParameters("Pulse.TaskbarBatteryPanel")
                {
                    ParentWindow = taskbar, WindowStyle = 0x40000000 | 0x04000000,
                    ExtendedWindowStyle = 0x08000080, Width = 1, Height = 1,
                    HwndSourceHook = MessageHook, TreatAsInputRoot = true, UsesPerPixelTransparency = true
                });
                _source.RootVisual = _panel;
                _source.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
                var enabled = 1;
                DwmSetWindowAttribute(_source.Handle, 12, ref enabled, sizeof(int)); // EXCLUDED_FROM_PEEK
                DwmSetWindowAttribute(_source.Handle, 3, ref enabled, sizeof(int)); // TRANSITIONS_FORCEDISABLED
            }
            finally { SetThreadDpiAwarenessContext(previousDpi); }
        }
        var scale = GetDpiForWindow(_source.Handle) / 96d;
        if (scale <= 0) scale = 1;
        var height = bar.Bottom - bar.Top;
        _source.CompositionTarget.BackgroundColor = Colors.Transparent;
        // Alpha zero makes a layered HWND pass clicks through to Explorer before WPF sees them.
        // Alpha 1 covers the complete rectangle without a visible background.
        _panel!.Background = HitBackground;
        _panel.DeviceIndicators.SetCurrentValue(UIElement.VisibilityProperty, _enabled ? Visibility.Visible : Visibility.Collapsed);
        var light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int setting && setting != 0;
        _panel.ApplySystemTheme(light);
        var layoutChanged = _layoutDirty || height != _lastHeight || scale != _lastScale;
        if (layoutChanged)
        {
            var wasStacked = _panel.IsStacked;
            _panel.Width = _panel.Height = double.NaN;
            _panel.IsStacked = false;
            _panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _panel.DeviceIndicators.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _horizontalWidth = Math.Max(1, (int)Math.Ceiling(_panel.DeviceIndicators.DesiredSize.Width * scale));
            _panel.IsStacked = true;
            _panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _panel.DeviceIndicators.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            // Keep the requested text size; a short taskbar must not clip the second row.
            _stackedWidth = _panel.DeviceIndicators.DesiredSize.Height <= height / scale
                ? Math.Max(1, (int)Math.Ceiling(_panel.DeviceIndicators.DesiredSize.Width * scale)) : 0;
            _panel.IsStacked = wasStacked;
            _lastHeight = height; _lastScale = scale; _layoutDirty = false;
        }
        var snapshot = _verificationSpace ?? _space.Latest;
        if (!snapshot.IsFresh || snapshot.Taskbar != taskbar ||
            snapshot.Bar != new Rect(bar.Left, bar.Top, bar.Right - bar.Left, bar.Bottom - bar.Top))
        { Hide("MeasuringSpace"); return; }
        var gap = (int)Math.Ceiling(4 * scale);
        var choice = TaskbarLayout.Choose(snapshot.ButtonsRight, notification.Left, _horizontalWidth, _stackedWidth, gap, snapshot.Obstacles, _viewModel.Settings.TaskbarLayout);
        _placement = choice.Placement;
        if (!_placement.Fits) { _recoveringFromFullTaskbar = true; _recoveryFitStarted = 0; Hide("InsufficientSpace"); return; }
        if (_recoveringFromFullTaskbar)
        {
            if (_recoveryFitStarted == 0) _recoveryFitStarted = Environment.TickCount64;
            if (Environment.TickCount64 - _recoveryFitStarted < 200) { Hide("WaitingForStableSpace"); return; }
        }
        _recoveringFromFullTaskbar = false;
        _mode = choice.Mode;
        _displayReason = _mode == TaskbarDisplayMode.Stacked ? "StackedIndicators" : "Indicators";
        var width = _placement.Width;
        if (layoutChanged || _panel.IsStacked != (_mode == TaskbarDisplayMode.Stacked) || _panel.Width != width / scale || _panel.Height != height / scale)
        {
            _panel.IsStacked = _mode == TaskbarDisplayMode.Stacked;
            _panel.Width = width / scale; _panel.Height = height / scale;
            _panel.Measure(new Size(width / scale, height / scale));
            _panel.Arrange(new Rect(0, 0, width / scale, height / scale));
            _panel.UpdateLayout();
        }
        var point = new PointNative { X = _placement.Left, Y = bar.Top };
        ScreenToClient(taskbar, ref point);
        GetWindowRect(_source.Handle, out var existing);
        if (existing.Left != _placement.Left || existing.Top != bar.Top || existing.Right - existing.Left != width || existing.Bottom - existing.Top != height || !IsWindowVisible(_source.Handle))
            SetWindowPos(_source.Handle, 0, point.X, point.Y, width, height, 0x0010 | 0x0040);
        SetAvailability(IsWindowVisible(_source.Handle));
    }

    public async Task WaitForSpaceAsync()
    {
        for (var attempt = 0; attempt < 35; attempt++)
        {
            Update();
            if (_space.Latest.IsFresh || !_enabled || _viewModel.TaskbarDevices.Count == 0) return;
            await Task.Delay(100);
        }
    }
    public async Task<bool> WriteVerificationAsync(string folder, bool trayVisible)
    {
        Update();
        var hwnd = _source is { IsDisposed: false } ? _source.Handle : 0;
        var tray = _taskbar == 0 ? 0 : FindWindowExW(_taskbar, 0, "TrayNotifyWnd", null);
        GetWindowRect(hwnd, out var panel); GetWindowRect(_taskbar, out var bar); GetWindowRect(tray, out var notification);
        var attached = hwnd != 0 && GetParent(hwnd) == _taskbar;
        var aligned = panel.Left == _placement.Left && panel.Right <= notification.Left && panel.Top == bar.Top && panel.Bottom == bar.Bottom;
        var visible = hwnd != 0 && IsWindowVisible(hwnd);
        Directory.CreateDirectory(folder);
        var fallbackAndRecovery = await VerifyFallbackRecoveryAsync(trayVisible, folder);
        var space = _space.Latest;
        var avoidsOverlap = !visible || space.IsFresh && panel.Left >= space.ButtonsRight &&
            !space.Obstacles.Any(obstacle => panel.Left < obstacle.Right && panel.Right > obstacle.Left);
        var hitPoints = new[] { new PointNative { X = panel.Left + 2, Y = panel.Top + 2 }, new PointNative { X = panel.Right - 2, Y = panel.Bottom - 2 }, new PointNative { X = (panel.Left + panel.Right) / 2, Y = panel.Top + 3 } };
        var fullAreaReceivesInput = hwnd != 0 && hitPoints.All(p => { var hit = WindowFromPoint(p); return hit == hwnd || IsChild(hwnd, hit); });
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "taskbar-layout.json"), JsonSerializer.Serialize(new
        {
            AttachedToTaskbar = attached, AlignedLeftOfTray = aligned, Visible = visible,
            NativeTaskbarChild = hwnd != 0 && IsChild(_taskbar, hwnd),
            PulseAnchorOnTaskbar = false, TrayIconVisible = trayVisible, AvoidsOverlap = avoidsOverlap,
            DisplayReason = _displayReason, SpaceKnown = space.IsFresh, SpaceError = space.Error,
            LayoutMode = _mode.ToString(), HorizontalWidth = _horizontalWidth, StackedWidth = _stackedWidth,
            ButtonsRight = space.ButtonsRight, Obstacles = space.Obstacles,
            SimulatedFullTaskbarHidesAndRecovers = fallbackAndRecovery,
            AdaptiveHorizontalStackedTrayTransitions = fallbackAndRecovery,
            Enabled = _enabled, TaskbarSetting = _viewModel.TaskbarPanel, ViewModelError = _viewModel.Error,
            FullAreaReceivesInput = fullAreaReceivesInput, Foreground = _panel?.Foreground.ToString(),
            Panel = new { panel.Left, panel.Top, panel.Right, panel.Bottom },
            Taskbar = new { bar.Left, bar.Top, bar.Right, bar.Bottom },
            Tray = new { notification.Left, notification.Top, notification.Right, notification.Bottom },
            ContentWidth = _panel?.ActualWidth, ContentHeight = _panel?.ActualHeight,
            Devices = _viewModel.TaskbarDevices.Select(d => new { d.Name, d.Level, d.Status, DisplayKind = d.Kind, AutomaticKind = d.Device.Kind })
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (bar.Right > bar.Left && bar.Bottom > bar.Top)
        {
            using var bitmap = new System.Drawing.Bitmap(bar.Right - bar.Left, bar.Bottom - bar.Top);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(bar.Left, bar.Top, 0, 0, bitmap.Size);
            bitmap.Save(Path.Combine(folder, "taskbar.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        return trayVisible && avoidsOverlap && fallbackAndRecovery && (!visible || attached && aligned && fullAreaReceivesInput);
    }
    private async Task<bool> VerifyFallbackRecoveryAsync(bool trayVisible, string folder)
    {
        var space = _space.Latest;
        if (!IsAvailable || !space.IsFresh) return trayVisible;
        var originalMode = _mode;
        try
        {
            var stackedShown = _mode == TaskbarDisplayMode.Stacked || _stackedWidth == 0 ||
                _stackedWidth >= _horizontalWidth && _viewModel.Settings.TaskbarLayout == TaskbarLayoutPreference.Horizontal;
            if (_horizontalWidth > _stackedWidth && _stackedWidth > 0)
            {
                var gap = (int)Math.Ceiling(4 * _lastScale);
                _verificationSpace = space with { ButtonsRight = space.TrayLeft - 2 * gap - _stackedWidth };
                Update();
                stackedShown = IsAvailable && _mode == TaskbarDisplayMode.Stacked;
                if (stackedShown)
                {
                    await Task.Delay(120);
                    GetWindowRect(_source!.Handle, out var bounds);
                    using var bitmap = new System.Drawing.Bitmap(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
                    using var graphics = System.Drawing.Graphics.FromImage(bitmap);
                    graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bitmap.Size);
                    bitmap.Save(Path.Combine(folder, "stacked-taskbar.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            _verificationSpace = space with { ButtonsRight = space.TrayLeft };
            Update();
            var hides = !IsAvailable && _displayReason == "InsufficientSpace" && trayVisible;
            _verificationSpace = null; Update();
            await Task.Delay(250);
            Update();
            return stackedShown && hides && IsAvailable && _mode == originalMode && trayVisible;
        }
        finally { _verificationSpace = null; }
    }
    public async Task<bool> WriteMenuVerificationAsync(string folder)
    {
        await WaitForSpaceAsync();
        Update();
        if (_panel is null || _source is null) return false;
        if (!IsAvailable) return true; // The tray owns the application menu when indicators do not fit.
        Directory.CreateDirectory(folder);
        _panel.OpenMenuForVerification(application: true);
        var menu = _panel.ContextMenu!;
        try
        {
            await Task.Delay(200);
            if (PresentationSource.FromVisual(menu) is not HwndSource popup) return false;
            GetWindowRect(_source.Handle, out var panelBounds);
            GetWindowRect(popup.Handle, out var appBounds);
            var exit = menu.Items.OfType<MenuItem>().Single(item => AutomationProperties.GetAutomationId(item) == "Taskbar.Exit");
            var exitBottom = exit.PointToScreen(new Point(exit.ActualWidth, exit.ActualHeight));
            var exitFits = exitBottom.Y <= appBounds.Bottom && appBounds.Bottom <= panelBounds.Top - 4;
            var rows = menu.Items.OfType<MenuItem>().Where(item => AutomationProperties.GetAutomationId(item).StartsWith("Taskbar.DeviceVisibility.", StringComparison.Ordinal)).ToArray();
            var rowsMatch = rows.Length == _viewModel.Devices.Count && rows.Zip(_viewModel.Devices).All(pair => pair.First.IsCheckable && pair.First.IsChecked == pair.Second.ShowOnTaskbar);
            CaptureMenu(folder, "application-menu.png", appBounds, panelBounds);
            bool SubmenuFits(MenuItem parent)
            {
                if (parent.Template.FindName("PART_Popup", parent) is not Popup { Child: FrameworkElement child } || PresentationSource.FromVisual(child) is not HwndSource submenu) return false;
                GetWindowRect(submenu.Handle, out var bounds);
                return bounds.Bottom <= panelBounds.Top - 4;
            }
            menu.IsOpen = false;
            // Let WPF detach the previous popup before opening the other menu scope.
            await Task.Delay(100);
            var deviceScoped = true;
            var iconFits = true;
            var devicesFit = true;
            if (_viewModel.TaskbarDevices.Count > 0)
            {
                _panel.OpenMenuForVerification();
                await Task.Delay(180);
                var actions = menu.Items.OfType<MenuItem>().Where(item => item.Visibility == Visibility.Visible)
                    .Select(AutomationProperties.GetAutomationId).Where(id => id.StartsWith("Taskbar.", StringComparison.Ordinal)).ToArray();
                deviceScoped = actions.SequenceEqual(["Taskbar.IconMenu", "Taskbar.TaskbarDevices"]);
                if (PresentationSource.FromVisual(menu) is HwndSource devicePopup)
                {
                    GetWindowRect(devicePopup.Handle, out var bounds);
                    deviceScoped &= bounds.Bottom <= panelBounds.Top - 4 && bounds.Bottom >= panelBounds.Top - 20;
                    CaptureMenu(folder, "device-menu.png", bounds, panelBounds);
                }
                _panel.IconSubmenu.IsSubmenuOpen = true;
                await Task.Delay(150);
                iconFits = SubmenuFits(_panel.IconSubmenu);
                _panel.IconSubmenu.IsSubmenuOpen = false;
                _panel.DevicesSubmenu.IsSubmenuOpen = true;
                await Task.Delay(150);
                devicesFit = SubmenuFits(_panel.DevicesSubmenu);
                if (_panel.DevicesSubmenu.Template.FindName("PART_Popup", _panel.DevicesSubmenu) is Popup { Child: FrameworkElement child } &&
                    PresentationSource.FromVisual(child) is HwndSource devicesPopup)
                {
                    GetWindowRect(devicesPopup.Handle, out var bounds);
                    CaptureMenu(folder, "shared-devices-menu.png", bounds, panelBounds);
                }
            }
            var removedRedundantItems = !menu.Items.OfType<MenuItem>().Any(item =>
                AutomationProperties.GetAutomationId(item) is "Taskbar.TogglePanel" or "Taskbar.Language");
            var passed = exitFits && rowsMatch && removedRedundantItems && deviceScoped && iconFits && devicesFit;
            File.WriteAllText(Path.Combine(folder, "menu-checks.json"), JsonSerializer.Serialize(new
            {
                Passed = passed, Language = Strings.Current.Language, ApplicationMenuAboveTaskbar = appBounds.Bottom <= panelBounds.Top - 4,
                ExitFullyVisible = exitFits, DeviceChecklistMatchesVisibility = rowsMatch,
                DeviceMenuOffersSharedDeviceList = deviceScoped, RedundantItemsRemoved = removedRedundantItems,
                IconSubmenuAboveTaskbar = iconFits, DevicesSubmenuAboveTaskbar = devicesFit
            }, new JsonSerializerOptions { WriteIndented = true }));
            return passed;
        }
        finally { menu.IsOpen = false; }
    }
    private static void CaptureMenu(string folder, string name, RectNative menu, RectNative panel)
    {
        var left = Math.Min(menu.Left, panel.Left);
        var top = Math.Min(menu.Top, panel.Top);
        var right = Math.Max(menu.Right, panel.Right);
        var bottom = Math.Max(menu.Bottom, panel.Bottom);
        using var bitmap = new System.Drawing.Bitmap(Math.Max(1, right - left), Math.Max(1, bottom - top));
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(left, top, 0, 0, bitmap.Size);
        bitmap.Save(Path.Combine(folder, name), System.Drawing.Imaging.ImageFormat.Png);
    }
    private static nint MessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0021) { handled = true; return 3; } // MA_NOACTIVATE
        return 0;
    }
    private void Hide(string reason) { _displayReason = reason; _mode = TaskbarDisplayMode.Hidden; if (_source is { IsDisposed: false } && IsWindow(_source.Handle)) ShowWindow(_source.Handle, 0); SetAvailability(false); }
    private void SetAvailability(bool available)
    {
        if (IsAvailable == available) return;
        IsAvailable = available; AvailabilityChanged?.Invoke(available);
    }
    private void TaskbarChanged(nint hook, uint kind, nint hwnd, int objectId, int childId, uint thread, uint time)
    {
        if (_disposed || hwnd == 0 || hwnd != _taskbar && !IsChild(_taskbar, hwnd)) return;
        // Explorer emits many intermediate events when a button is added or resized.
        // Keep the current frame until the worker has a replacement measurement.
        _space.RequestRefresh();
    }
    private void ReleaseHost()
    {
        if (_structureHook != 0) UnhookWinEvent(_structureHook);
        if (_locationHook != 0) UnhookWinEvent(_locationHook);
        _structureHook = _locationHook = 0;
        _panel?.Dispose(); _source?.Dispose(); _source = null; _panel = null; _taskbar = 0; _layoutDirty = true;
    }
    private void InvalidateLayout()
    {
        _layoutDirty = true;
        if (_pendingLayout is not null) return;
        // ItemsControl applies collection changes during data binding. Measure again afterwards
        // so sizing reflects the latest device collection before the collision check.
        _pendingLayout = Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _pendingLayout = null; _layoutDirty = true; Update();
        });
    }
    private static Brush CreateHitBackground() { var brush = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); brush.Freeze(); return brush; }
    public void Dispose() { _disposed = true; _space.Dispose(); _timer.Stop(); _pendingLayout?.Abort(); _viewModel.DevicesChanged -= InvalidateLayout; ReleaseHost(); }

    [StructLayout(LayoutKind.Sequential)] private struct RectNative { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct PointNative { public int X, Y; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowW(string className, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowExW(nint parent, nint after, string className, string? name);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out RectNative rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint hwnd, int show);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetParent(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindowDpiAwarenessContext(nint hwnd);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(PointNative point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out PointNative point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ScreenToClient(nint hwnd, ref PointNative point);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, uint attribute, ref int value, int size);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsChild(nint parent, nint child);
    private delegate void WinEventProc(nint hook, uint kind, nint hwnd, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint first, uint last, nint module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWinEvent(nint hook);
}

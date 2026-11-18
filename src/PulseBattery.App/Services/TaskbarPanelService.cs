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
    private TaskbarIndicatorSize _lastSize;
    public bool IsAvailable { get; private set; }
    public event Action<bool>? AvailabilityChanged;
    private bool _layoutDirty = true;
    private DispatcherOperation? _pendingLayout;
    private int _lastHeight;
    private double _lastScale;
    private int _width;
    private static readonly Brush HitBackground = CreateHitBackground();
    public TaskbarPanelService(DashboardViewModel viewModel, Action open)
    {
        _viewModel = viewModel; _open = open;
        _viewModel.DevicesChanged += InvalidateLayout;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Update();
        _timer.Start();
    }
    public void SetEnabled(bool enabled)
    {
        if (_enabled != enabled || _lastSize != _viewModel.TaskbarSize) InvalidateLayout();
        _enabled = enabled; _lastSize = _viewModel.TaskbarSize; Update();
    }

    public void Update()
    {
        var taskbar = FindWindowW("Shell_TrayWnd", null);
        var tray = taskbar == 0 ? 0 : FindWindowExW(taskbar, 0, "TrayNotifyWnd", null);
        if (taskbar == 0 || tray == 0 || !IsWindowVisible(taskbar)) { Hide(); return; }
        if (!GetWindowRect(taskbar, out var bar) || !GetWindowRect(tray, out var notification) || bar.Right - bar.Left < bar.Bottom - bar.Top) { Hide(); return; }
        if (_source is null || _source.IsDisposed || !IsWindow(_source.Handle) || _taskbar != taskbar)
        {
            ReleaseHost();
            _taskbar = taskbar;
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
        if (_layoutDirty || height != _lastHeight || scale != _lastScale)
        {
            _panel.Width = double.NaN;
            _panel.Measure(new Size(double.PositiveInfinity, height / scale));
            _width = Math.Max(1, (int)Math.Ceiling(_panel.DesiredSize.Width * scale));
            _panel.Width = _width / scale; _panel.Height = height / scale;
            _panel.Arrange(new Rect(0, 0, _width / scale, height / scale));
            _panel.UpdateLayout();
            _lastHeight = height; _lastScale = scale; _layoutDirty = false;
        }
        var width = _width;
        var point = new PointNative { X = notification.Left - width - (int)Math.Ceiling(4 * scale), Y = bar.Top };
        ScreenToClient(taskbar, ref point);
        GetWindowRect(_source.Handle, out var existing);
        if (existing.Left != notification.Left - width - (int)Math.Ceiling(4 * scale) || existing.Top != bar.Top || existing.Right - existing.Left != width || existing.Bottom - existing.Top != height || !IsWindowVisible(_source.Handle))
            SetWindowPos(_source.Handle, 0, point.X, point.Y, width, height, 0x0010 | 0x0040);
        SetAvailability(IsWindowVisible(_source.Handle));
    }

    public bool WriteVerification(string folder)
    {
        Update();
        var hwnd = _source is { IsDisposed: false } ? _source.Handle : 0;
        var tray = _taskbar == 0 ? 0 : FindWindowExW(_taskbar, 0, "TrayNotifyWnd", null);
        GetWindowRect(hwnd, out var panel); GetWindowRect(_taskbar, out var bar); GetWindowRect(tray, out var notification);
        var attached = hwnd != 0 && GetParent(hwnd) == _taskbar;
        var aligned = panel.Right <= notification.Left && notification.Left - panel.Right <= 20 && panel.Top == bar.Top && panel.Bottom == bar.Bottom;
        var visible = hwnd != 0 && IsWindowVisible(hwnd);
        var hitPoints = new[] { new PointNative { X = panel.Left + 2, Y = panel.Top + 2 }, new PointNative { X = panel.Right - 2, Y = panel.Bottom - 2 }, new PointNative { X = (panel.Left + panel.Right) / 2, Y = panel.Top + 3 } };
        var fullAreaReceivesInput = hwnd != 0 && hitPoints.All(p => { var hit = WindowFromPoint(p); return hit == hwnd || IsChild(hwnd, hit); });
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "taskbar-layout.json"), JsonSerializer.Serialize(new
        {
            AttachedToTaskbar = attached, AlignedLeftOfTray = aligned, Visible = visible,
            NativeTaskbarChild = hwnd != 0 && IsChild(_taskbar, hwnd),
            PersistentPulseAnchor = _panel?.PulseAnchor.Visibility == Visibility.Visible && visible,
            Enabled = _enabled, TaskbarSetting = _viewModel.TaskbarPanel, ViewModelError = _viewModel.Error,
            FullAreaReceivesInput = fullAreaReceivesInput, Foreground = _panel?.Foreground.ToString(),
            Panel = new { panel.Left, panel.Top, panel.Right, panel.Bottom },
            Taskbar = new { bar.Left, bar.Top, bar.Right, bar.Bottom },
            Tray = new { notification.Left, notification.Top, notification.Right, notification.Bottom },
            ContentWidth = _panel?.ActualWidth, ContentHeight = _panel?.ActualHeight,
            Devices = _viewModel.TaskbarDevices.Select(d => new { d.Name, d.Level, d.Status, DisplayKind = d.Kind, AutomaticKind = d.Device.Kind })
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (attached && visible && bar.Right > bar.Left && bar.Bottom > bar.Top)
        {
            using var bitmap = new System.Drawing.Bitmap(bar.Right - bar.Left, bar.Bottom - bar.Top);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(bar.Left, bar.Top, 0, 0, bitmap.Size);
            bitmap.Save(Path.Combine(folder, "taskbar.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        return attached && aligned && visible && fullAreaReceivesInput;
    }
    public async Task<bool> WriteMenuVerificationAsync(string folder)
    {
        Update();
        if (_panel is null || _source is null) return false;
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
            _panel.LanguageSubmenu.IsSubmenuOpen = true;
            await Task.Delay(150);
            bool SubmenuFits(MenuItem parent)
            {
                if (parent.Template.FindName("PART_Popup", parent) is not Popup { Child: FrameworkElement child } || PresentationSource.FromVisual(child) is not HwndSource submenu) return false;
                GetWindowRect(submenu.Handle, out var bounds);
                return bounds.Bottom <= panelBounds.Top - 4;
            }
            var languageFits = SubmenuFits(_panel.LanguageSubmenu);
            menu.IsOpen = false;
            // Let WPF detach the previous popup before opening the other menu scope.
            await Task.Delay(100);
            var deviceScoped = true;
            var iconFits = true;
            if (_viewModel.TaskbarDevices.Count > 0)
            {
                _panel.OpenMenuForVerification();
                await Task.Delay(180);
                var actions = menu.Items.OfType<MenuItem>().Where(item => item.Visibility == Visibility.Visible)
                    .Select(AutomationProperties.GetAutomationId).Where(id => id.StartsWith("Taskbar.", StringComparison.Ordinal)).ToArray();
                deviceScoped = actions.SequenceEqual(["Taskbar.IconMenu", "Taskbar.HideDevice"]);
                if (PresentationSource.FromVisual(menu) is HwndSource devicePopup)
                {
                    GetWindowRect(devicePopup.Handle, out var bounds);
                    deviceScoped &= bounds.Bottom <= panelBounds.Top - 4 && bounds.Bottom >= panelBounds.Top - 20;
                    CaptureMenu(folder, "device-menu.png", bounds, panelBounds);
                }
                _panel.IconSubmenu.IsSubmenuOpen = true;
                await Task.Delay(150);
                iconFits = SubmenuFits(_panel.IconSubmenu);
            }
            var passed = exitFits && rowsMatch && languageFits && deviceScoped && iconFits;
            File.WriteAllText(Path.Combine(folder, "menu-checks.json"), JsonSerializer.Serialize(new
            {
                Passed = passed, Language = Strings.Current.Language, ApplicationMenuAboveTaskbar = appBounds.Bottom <= panelBounds.Top - 4,
                ExitFullyVisible = exitFits, DeviceChecklistMatchesVisibility = rowsMatch,
                DeviceMenuContainsOnlyDeviceActions = deviceScoped, LanguageSubmenuAboveTaskbar = languageFits, IconSubmenuAboveTaskbar = iconFits
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
    private void Hide() { if (_source is { IsDisposed: false } && IsWindow(_source.Handle)) ShowWindow(_source.Handle, 0); SetAvailability(false); }
    private void SetAvailability(bool available)
    {
        if (IsAvailable == available) return;
        IsAvailable = available; AvailabilityChanged?.Invoke(available);
    }
    private void ReleaseHost() { _panel?.Dispose(); _source?.Dispose(); _source = null; _panel = null; _taskbar = 0; _layoutDirty = true; }
    private void InvalidateLayout()
    {
        _layoutDirty = true;
        if (_pendingLayout is not null) return;
        // ItemsControl applies collection changes during data binding. Measure again afterwards
        // so removing the final device leaves exactly the Pulse button's width.
        _pendingLayout = Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _pendingLayout = null; _layoutDirty = true; Update();
        });
    }
    private static Brush CreateHitBackground() { var brush = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); brush.Freeze(); return brush; }
    public void Dispose() { _timer.Stop(); _pendingLayout?.Abort(); _viewModel.DevicesChanged -= InvalidateLayout; ReleaseHost(); }

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
}

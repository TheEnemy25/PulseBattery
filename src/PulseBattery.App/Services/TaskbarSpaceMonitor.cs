using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using PulseBattery.Core;

namespace PulseBattery.App.Services;

internal sealed record TaskbarSpaceSnapshot(nint Taskbar, Rect Bar, int TrayLeft, int ButtonsRight,
    TaskbarSpan[] Obstacles, long MeasuredAt, bool Known, string? Error = null)
{
    public static TaskbarSpaceSnapshot Unknown { get; } = new(0, Rect.Empty, 0, 0, [], 0, false);
    public bool IsFresh => Known && Environment.TickCount64 - MeasuredAt < 6000;
}

// UI Automation can call back into Explorer and this application's child HWND.
// Never query it or wait for this worker from the WPF UI thread.
internal sealed class TaskbarSpaceMonitor : IDisposable
{
    private readonly object _gate = new();
    private readonly Action _changed;
    private readonly Thread _thread;
    private volatile TaskbarSpaceSnapshot _latest = TaskbarSpaceSnapshot.Unknown;
    private nint _target;
    private bool _stopping;
    private long _generation;
    public TaskbarSpaceSnapshot Latest => _latest;

    public TaskbarSpaceMonitor(Action changed)
    {
        _changed = changed;
        _thread = new Thread(Run) { IsBackground = true, Name = "Pulse taskbar space" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }
    public void Watch(nint taskbar)
    {
        lock (_gate)
        {
            if (_target == taskbar) return;
            _target = taskbar; _latest = TaskbarSpaceSnapshot.Unknown; _generation++;
            Monitor.Pulse(_gate);
        }
    }
    public void RequestRefresh()
    {
        lock (_gate)
        {
            // Keep the last measurement usable while Explorer publishes its new layout.
            _generation++;
            Monitor.Pulse(_gate);
        }
    }
    private void Run()
    {
        while (true)
        {
            nint taskbar;
            long generation;
            lock (_gate)
            {
                while (!_stopping && _target == 0) Monitor.Wait(_gate);
                if (_stopping) return;
                // Coalesce the burst of native events produced by one Explorer layout.
                Monitor.Wait(_gate, 80);
                if (_stopping) return;
                if (_target == 0) continue;
                taskbar = _target;
                generation = _generation;
            }
            TaskbarSpaceSnapshot snapshot;
            try { snapshot = Measure(taskbar); }
            catch (Exception error) when (error is COMException or ElementNotAvailableException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
            { snapshot = TaskbarSpaceSnapshot.Unknown with { Taskbar = taskbar, Error = error.Message }; }
            lock (_gate)
            {
                if (_stopping) return;
                if (_target != taskbar || _generation != generation) continue;
                var previous = _latest;
                // An accessibility peer can disappear midway through Explorer's animation.
                // A transient failure must not clear a still-recent rendered placement.
                if (!snapshot.Known && previous.IsFresh && previous.Taskbar == taskbar)
                    snapshot = previous with { Error = snapshot.Error };
                _latest = snapshot;
                if (previous.Known != snapshot.Known || previous.Bar != snapshot.Bar || previous.TrayLeft != snapshot.TrayLeft ||
                    previous.ButtonsRight != snapshot.ButtonsRight || !previous.Obstacles.SequenceEqual(snapshot.Obstacles))
                    _changed();
                // Explorer events wake this thread immediately. Poll infrequently as
                // a backstop for third-party widgets and missed accessibility events.
                Monitor.Wait(_gate, 4000);
            }
        }
    }
    private static TaskbarSpaceSnapshot Measure(nint taskbar)
    {
        var tray = FindWindowExW(taskbar, 0, "TrayNotifyWnd", null);
        if (tray == 0 || !GetWindowRect(taskbar, out var bar) || !GetWindowRect(tray, out var notification))
            return TaskbarSpaceSnapshot.Unknown;
        var obstacles = new List<TaskbarSpan>();
        var right = bar.Left;
        var buttons = 0;
        var root = AutomationElement.FromHandle(taskbar);
        var request = new CacheRequest { AutomationElementMode = AutomationElementMode.None, TreeScope = TreeScope.Element };
        request.Add(AutomationElement.BoundingRectangleProperty);
        request.Add(AutomationElement.AutomationIdProperty);
        request.Add(AutomationElement.ProcessIdProperty);
        request.Add(AutomationElement.ClassNameProperty);
        var condition = new AndCondition(new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
            new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.ClassNameProperty, "Taskbar.TaskListButtonAutomationPeer")));
        AutomationElementCollection elements;
        using (request.Activate()) elements = root.FindAll(TreeScope.Descendants, condition);
        foreach (AutomationElement element in elements)
        {
            var data = element.Cached;
            var bounds = data.BoundingRectangle;
            if (data.ProcessId == Environment.ProcessId || data.ClassName.StartsWith("SystemTray.", StringComparison.Ordinal) || bounds.IsEmpty || !double.IsFinite(bounds.Right) ||
                bounds.Width <= 0 || bounds.Right <= bar.Left || bounds.Left >= notification.Left ||
                bounds.Top >= bar.Bottom || bounds.Bottom <= bar.Top) continue;
            var span = new TaskbarSpan((int)Math.Floor(bounds.Left), Math.Min(notification.Left, (int)Math.Ceiling(bounds.Right)));
            if (data.AutomationId == "WidgetsButton") obstacles.Add(span);
            else { right = Math.Max(right, span.Right); buttons++; }
        }
        // Other taskbar widgets can be native child windows without UI Automation peers.
        GetWindowThreadProcessId(taskbar, out var explorer);
        EnumChildWindows(taskbar, (hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == explorer || owner == Environment.ProcessId || !IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out var bounds)) return true;
            if (bounds.Right > bar.Left && bounds.Left < notification.Left && bounds.Bottom > bar.Top && bounds.Top < bar.Bottom)
                obstacles.Add(new(bounds.Left, Math.Min(notification.Left, bounds.Right)));
            return true;
        }, 0);
        return new(taskbar, new Rect(bar.Left, bar.Top, bar.Right - bar.Left, bar.Bottom - bar.Top), notification.Left,
            right, obstacles.Distinct().OrderBy(span => span.Left).ToArray(), Environment.TickCount64, buttons > 0);
    }
    public void Dispose()
    {
        lock (_gate) { _stopping = true; Monitor.Pulse(_gate); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    private delegate bool EnumProc(nint hwnd, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowExW(nint parent, nint after, string name, string? title);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumChildWindows(nint hwnd, EnumProc callback, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint hwnd);
}

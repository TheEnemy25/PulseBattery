using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Drawing.Imaging;
using PulseBattery.App.ViewModels;
using PulseBattery.Core;
using PulseBattery.Infrastructure;
using Microsoft.Win32;

namespace PulseBattery.App.Services;

internal static class TaskbarMotionChecks
{
    public static async Task<bool> RunAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var store = new JsonSettingsStore(Path.Combine(folder, "isolated-motion-settings.json"));
        store.Save(new AppSettings { StartWithWindows = false, TaskbarSpacing = 0 });
        var model = new DashboardViewModel(new BatteryMonitor([new FixtureProvider()]), store, default);
        await model.RefreshAsync();
        using var service = new TaskbarPanelService(model, () => { });
        model.SettingsChanged += () => service.SetEnabled(model.TaskbarPanel);
        model.DevicesChanged += service.Update;
        service.SetEnabled(true);
        await service.WaitForSpaceAsync();
        await Task.Delay(400);
        if (!service.IsAvailable) throw new InvalidOperationException("Motion checks need free space on an interactive taskbar.");
        var handle = service.WindowHandle;
        var light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int theme && theme != 0;
        var results = new List<object>();
        async Task Sample(string name, Func<Task> action)
        {
            using var stop = new CancellationTokenSource();
            var sampling = Task.Run(async () =>
            {
                var frames = 0; var hidden = 0; var blank = 0; var minimum = int.MaxValue;
                while (!stop.IsCancellationRequested)
                {
                    frames++;
                    if (!IsWindowVisible(handle)) hidden++;
                    else if (GetWindowRect(handle, out var bounds) && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top)
                    {
                        using var bitmap = new System.Drawing.Bitmap(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
                        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bitmap.Size);
                        var count = GlyphPixels(bitmap, light);
                        minimum = Math.Min(minimum, count);
                        if (count < 10) blank++;
                    }
                    await Task.Delay(8);
                }
                return new { Transition = name, Frames = frames, HiddenFrames = hidden, BlankFrames = blank, MinimumGlyphPixels = minimum };
            });
            try { await action(); await Task.Delay(400); }
            finally { stop.Cancel(); }
            var result = await sampling;
            results.Add(result);
            if (result.HiddenFrames != 0 || result.BlankFrames != 0)
                throw new InvalidOperationException($"Panel blinked during {name}: hidden={result.HiddenFrames}, blank={result.BlankFrames}.");
        }
        try
        {
            await Sample("Add and remove a real taskbar button", async () =>
            {
                var window = new Window { Title = "Pulse taskbar motion check", Width = 300, Height = 140,
                    Left = 30, Top = 100, ShowInTaskbar = true, Content = "Temporary taskbar check" };
                try { window.Show(); await Task.Delay(1000); }
                finally { window.Close(); }
                await Task.Delay(600);
            });
            using var session = new DashboardSession(model, Application.Current.Dispatcher, default, _ => { }, () => { });
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!session.IsReady && session.IsRunning && DateTime.UtcNow < deadline) await Task.Delay(50);
            if (!session.IsReady) throw new InvalidOperationException("Dashboard did not open.");
            using var child = Process.GetProcessById(session.ProcessId);
            child.Refresh(); var windowHandle = child.MainWindowHandle;
            await Task.Run(() => ((InvokePattern)Find(AutomationElement.FromHandle(windowHandle), "OpenSettings")
                .GetCurrentPattern(InvokePattern.Pattern)).Invoke());
            await Task.Delay(400);
            await Sample("Drag scale and spacing through dashboard IPC", () => Task.Run(async () =>
            {
                var window = AutomationElement.FromHandle(windowHandle);
                var scale = (RangeValuePattern)Find(window, "TaskbarScale").GetCurrentPattern(RangeValuePattern.Pattern);
                var spacing = (RangeValuePattern)Find(window, "TaskbarSpacing").GetCurrentPattern(RangeValuePattern.Pattern);
                foreach (var value in Enumerable.Range(80, 71).Where(value => value % 2 == 0))
                { scale.SetValue(value); await Task.Delay(20); }
                foreach (var value in Enumerable.Range(0, 25))
                { spacing.SetValue(value); await Task.Delay(20); }
                foreach (var value in Enumerable.Range(0, 25).Reverse())
                { spacing.SetValue(value); await Task.Delay(20); }
                scale.SetValue(100);
            }));
            await Sample("Switch to compact and drag its independent sliders", () => Task.Run(async () =>
            {
                var window = AutomationElement.FromHandle(windowHandle);
                ((RangeValuePattern)Find(window, "TaskbarLayout").GetCurrentPattern(RangeValuePattern.Pattern)).SetValue(1);
                var scale = (RangeValuePattern)Find(window, "TaskbarCompactScale").GetCurrentPattern(RangeValuePattern.Pattern);
                var spacing = (RangeValuePattern)Find(window, "TaskbarCompactSpacing").GetCurrentPattern(RangeValuePattern.Pattern);
                foreach (var value in Enumerable.Range(80, 71).Where(value => value % 2 == 0))
                { scale.SetValue(value); await Task.Delay(20); }
                foreach (var value in Enumerable.Range(0, 25))
                { spacing.SetValue(value); await Task.Delay(20); }
                scale.SetValue(100); spacing.SetValue(8);
                ((RangeValuePattern)Find(window, "TaskbarLayout").GetCurrentPattern(RangeValuePattern.Pattern)).SetValue(0);
            }));
            session.CloseWindow();
            return Write(true, null);
        }
        catch (Exception error) { return Write(false, error.ToString()); }
        bool Write(bool passed, string? error)
        {
            File.WriteAllText(Path.Combine(folder, "motion-checks.json"), JsonSerializer.Serialize(new
            { Passed = passed, IsolatedSettings = true, Results = results, Error = error }, new JsonSerializerOptions { WriteIndented = true }));
            return passed;
        }
    }
    private static int GlyphPixels(System.Drawing.Bitmap bitmap, bool light)
    {
        var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[data.Stride * bitmap.Height]; Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            var count = 0;
            for (var index = 0; index < pixels.Length; index += 4)
                if (light ? pixels[index] + pixels[index + 1] + pixels[index + 2] < 195 :
                    pixels[index] + pixels[index + 1] + pixels[index + 2] > 570) count++;
            return count;
        }
        finally { bitmap.UnlockBits(data); }
    }
    private static AutomationElement Find(AutomationElement window, string id) => window.FindFirst(TreeScope.Descendants,
        new PropertyCondition(AutomationElement.AutomationIdProperty, id)) ?? throw new InvalidOperationException($"Missing control: {id}");
    private sealed class FixtureProvider : IBatteryProvider
    {
        public Task<IReadOnlyList<BatteryDevice>> GetDevicesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<BatteryDevice>>([
            new("motion:mouse", "Motion check mouse", DeviceKind.Mouse, 74, false, ReadingState.Available, "test", "test")]);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint hwnd);
}

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace PulseBattery.App;

public partial class App
{
    private async Task<bool> CheckBackgroundAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        await Task.Delay(1500);
        var backgroundStartsWithoutDashboard = _dashboard is null;
        var background = MemorySnapshot();
        using var process = Process.GetCurrentProcess();
        var idleStart = process.TotalProcessorTime;
        await Task.Delay(3000);
        process.Refresh();
        var idleCpuMilliseconds = (process.TotalProcessorTime - idleStart).TotalMilliseconds;
        var closedWindows = new List<WeakReference>();
        var samples = new List<object>();
        var opensOneWindow = true;
        var closesWindow = true;
        for (var cycle = 0; cycle < 3; cycle++)
        {
            OpenDashboardForCheck();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(300);
            opensOneWindow &= Windows.OfType<Views.Dashboard>().Count() == 1 && _dashboard?.IsVisible == true;
            samples.Add(new { Cycle = cycle + 1, Open = MemorySnapshot() });
            closedWindows.Add(CloseDashboardForCheck());
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(300);
            closesWindow &= _dashboard is null && !Windows.OfType<Views.Dashboard>().Any();
        }
        // Diagnostic collection only: prove that no application root retains closed windows.
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        await Task.Delay(300);
        var closedWindowsCollected = closedWindows.All(reference => !reference.IsAlive);
        var afterClosing = MemorySnapshot();
        var passed = backgroundStartsWithoutDashboard && opensOneWindow && closesWindow && closedWindowsCollected;
        File.WriteAllText(Path.Combine(folder, "background-checks.json"), JsonSerializer.Serialize(new
        {
            Passed = passed, BackgroundStartsWithoutDashboard = backgroundStartsWithoutDashboard,
            RepeatedOpenReusesOneWindow = opensOneWindow, CloseDestroysDashboard = closesWindow,
            ClosedWindowsCollected = closedWindowsCollected, Background = background,
            IdleSampleSeconds = 3, IdleCpuMilliseconds = idleCpuMilliseconds,
            OpenSamples = samples, AfterClosing = afterClosing,
            Note = "Same-process WPF measurements; working set includes shared pages. GC is forced only for this diagnostic."
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void OpenDashboardForCheck() { ShowDashboard(); ShowDashboard(); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference CloseDashboardForCheck()
    {
        var reference = new WeakReference(_dashboard!);
        _dashboard!.Close();
        return reference;
    }

    private static object MemorySnapshot()
    {
        using var process = Process.GetCurrentProcess();
        return new
        {
            PrivateBytes = process.PrivateMemorySize64, WorkingSetBytes = process.WorkingSet64,
            PrivateWorkingSetBytes = PrivateWorkingSet(process),
            ManagedBytes = GC.GetTotalMemory(false), Handles = process.HandleCount,
            CpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds
        };
    }

    private static long PrivateWorkingSet(Process process)
    {
        // PSAPI_WORKING_SET_BLOCK.Shared is bit 8. Count only non-shareable resident pages.
        var slots = checked((int)(process.WorkingSet64 / Environment.SystemPageSize) + 4096);
        for (var attempt = 0; attempt < 3; attempt++, slots *= 2)
        {
            var bytes = checked((slots + 1) * IntPtr.Size);
            var buffer = Marshal.AllocHGlobal(bytes);
            try
            {
                if (!QueryWorkingSet(process.Handle, buffer, bytes)) continue;
                var count = checked((int)Marshal.ReadIntPtr(buffer).ToInt64());
                long pages = 0;
                for (var index = 1; index <= count; index++)
                    if ((Marshal.ReadIntPtr(buffer, index * IntPtr.Size).ToInt64() & 0x100) == 0) pages++;
                return pages * Environment.SystemPageSize;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryWorkingSet(nint process, nint buffer, int size);
}

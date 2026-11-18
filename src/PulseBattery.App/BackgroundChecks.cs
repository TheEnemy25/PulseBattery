using System.Diagnostics;
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
        var backgroundStartsWithoutDashboard = _dashboardSession is null && !Windows.OfType<Views.Dashboard>().Any();
        var background = MemorySnapshot();
        using var process = Process.GetCurrentProcess();
        var idleStart = process.TotalProcessorTime;
        await Task.Delay(3000);
        process.Refresh();
        var idleCpuMilliseconds = (process.TotalProcessorTime - idleStart).TotalMilliseconds;
        var samples = new List<object>();
        var opensOneWindow = true;
        var closesWindow = true;
        var residentNeverConstructsDashboard = true;
        for (var cycle = 0; cycle < 3; cycle++)
        {
            ShowDashboard();
            var session = _dashboardSession!;
            ShowDashboard();
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!session.IsReady && session.IsRunning && DateTime.UtcNow < deadline) await Task.Delay(50);
            await Task.Delay(300);
            opensOneWindow &= ReferenceEquals(session, _dashboardSession) && session.IsReady && session.IsRunning;
            residentNeverConstructsDashboard &= !Windows.OfType<Views.Dashboard>().Any();
            if (!session.IsRunning) { closesWindow = false; break; }
            using var child = Process.GetProcessById(session.ProcessId);
            samples.Add(new { Cycle = cycle + 1, Resident = MemorySnapshot(), Dashboard = new
            {
                PrivateBytes = child.PrivateMemorySize64, WorkingSetBytes = child.WorkingSet64,
                PrivateWorkingSetBytes = PrivateWorkingSet(child)
            } });
            session.CloseWindow();
            deadline = DateTime.UtcNow.AddSeconds(10);
            while (!child.HasExited && DateTime.UtcNow < deadline) await Task.Delay(50);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(300);
            closesWindow &= child.HasExited && _dashboardSession is null;
        }
        var afterClosingBeforeCollection = MemorySnapshot();
        var trayAvailable = _tray?.IsVisible == true;
        var passed = backgroundStartsWithoutDashboard && opensOneWindow && closesWindow && residentNeverConstructsDashboard && trayAvailable;
        File.WriteAllText(Path.Combine(folder, "background-checks.json"), JsonSerializer.Serialize(new
        {
            Passed = passed, BackgroundStartsWithoutDashboard = backgroundStartsWithoutDashboard,
            RepeatedOpenReusesOneWindow = opensOneWindow, CloseDestroysDashboard = closesWindow,
            DashboardProcessesExit = closesWindow, ResidentNeverConstructsDashboard = residentNeverConstructsDashboard, Background = background,
            TrayRemainsAvailable = trayAvailable, AfterClosingBeforeCollection = afterClosingBeforeCollection,
            IdleSampleSeconds = 3, IdleCpuMilliseconds = idleCpuMilliseconds,
            OpenSamples = samples,
            Note = "No forced collection or working-set trimming. The resident monitor and temporary dashboard are measured separately; both count while the window is open."
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed;
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

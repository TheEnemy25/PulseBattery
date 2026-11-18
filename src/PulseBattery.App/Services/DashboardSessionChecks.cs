using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using PulseBattery.App.Localization;
using PulseBattery.App.ViewModels;
using PulseBattery.Core;

namespace PulseBattery.App.Services;

// Drive the actual child window; fixture settings never touch the user's file.
internal static class DashboardSessionChecks
{
    public static async Task<bool> RunAsync(string folder)
    {
        var language = Strings.Current.Language;
        var store = new MemoryStore();
        var provider = new FixtureProvider();
        var model = new DashboardViewModel(new BatteryMonitor([provider]), store, default);
        await model.RefreshAsync();
        var closed = false;
        var exitRequested = false;
        var samples = new Dictionary<string, bool>();
        try
        {
            using var session = new DashboardSession(model, Application.Current.Dispatcher, default,
                _ => closed = true, () => exitRequested = true);
            samples["WindowReady"] = await Until(() => session.IsReady && session.IsRunning);
            if (!samples["WindowReady"]) return Write(false);
            using var child = Process.GetProcessById(session.ProcessId);
            child.Refresh();
            var handle = child.MainWindowHandle;
            await Task.Run(() =>
            {
                var window = AutomationElement.FromHandle(handle);
                ((InvokePattern)Find(window, "OpenSettings").GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                var scale = (RangeValuePattern)Find(window, "TaskbarScale").GetCurrentPattern(RangeValuePattern.Pattern);
                var spacing = (RangeValuePattern)Find(window, "TaskbarSpacing").GetCurrentPattern(RangeValuePattern.Pattern);
                foreach (var value in new[] { 80, 103, 140, 150 }) scale.SetValue(value);
                foreach (var value in new[] { 0, 2, 15, 24 }) spacing.SetValue(value);
                ((SelectionItemPattern)Find(window, "TaskbarLayout.Compact").GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                ((RangeValuePattern)Find(window, "TaskbarCompactScale").GetCurrentPattern(RangeValuePattern.Pattern)).SetValue(100);
                ((RangeValuePattern)Find(window, "TaskbarCompactSpacing").GetCurrentPattern(RangeValuePattern.Pattern)).SetValue(8);
                var selector = Find(window, "LanguageSelector");
                ((ExpandCollapsePattern)selector.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
                var choices = selector.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Cast<AutomationElement>().ToArray();
                var ukrainian = choices.Single(item => item.Current.Name.Contains("Українська", StringComparison.Ordinal));
                ((SelectionItemPattern)ukrainian.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            });
            samples["RapidSettingsAndLanguageReachMonitor"] = await Until(() =>
                model.TaskbarScalePercent == 150 && model.TaskbarSpacing == 24 && model.Language == "uk" &&
                store.Settings is { TaskbarScalePercent: 150, TaskbarSpacing: 24, Language: "uk" });
            samples["IndependentCompactProfileAndModeReachMonitor"] = await Until(() => model.TaskbarCompactScalePercent == 100 &&
                model.TaskbarCompactSpacing == 8 && model.TaskbarLayoutMode == 1 &&
                store.Settings is { TaskbarCompactScalePercent: 100, TaskbarCompactSpacing: 8, TaskbarLayout: TaskbarLayoutPreference.Compact });
            model.TaskbarScalePercent = 127; model.TaskbarSpacing = 17;
            await Task.Delay(200);
            samples["MonitorChangesReachWindow"] = await Task.Run(() =>
            {
                var window = AutomationElement.FromHandle(handle);
                ((SelectionItemPattern)Find(window, "TaskbarLayout.Horizontal").GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                return ((RangeValuePattern)Find(window, "TaskbarScale").GetCurrentPattern(RangeValuePattern.Pattern)).Current.Value == 127 &&
                    ((RangeValuePattern)Find(window, "TaskbarSpacing").GetCurrentPattern(RangeValuePattern.Pattern)).Current.Value == 17;
            });
            await Task.Run(() =>
            {
                var window = AutomationElement.FromHandle(handle);
                ((InvokePattern)Find(window, "OpenSettings").GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                ((TogglePattern)Find(window, "DeviceTaskbarVisibility").GetCurrentPattern(TogglePattern.Pattern)).Toggle();
                ((InvokePattern)Find(window, "DashboardRefresh").GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            });
            samples["DeviceVisibilityReachesMonitor"] = await Until(() => model.TaskbarDevices.Count == 0);
            samples["RefreshRunsOnlyInMonitor"] = await Until(() => provider.Reads >= 2 && !model.IsBusy);
            session.Activate(); session.CloseWindow();
            samples["CloseEndsChildProcess"] = await Until(() => closed && child.HasExited);
            samples["ClosingWindowKeepsMonitorAlive"] = !exitRequested;
            closed = false;
            using var reopened = new DashboardSession(model, Application.Current.Dispatcher, default,
                _ => closed = true, () => exitRequested = true);
            samples["WindowReopens"] = await Until(() => reopened.IsReady && reopened.IsRunning);
            if (!samples["WindowReopens"]) return Write(false);
            using var reopenedChild = Process.GetProcessById(reopened.ProcessId);
            reopenedChild.Refresh();
            await Task.Run(() =>
            {
                var window = AutomationElement.FromHandle(reopenedChild.MainWindowHandle);
                var exit = window.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.NameProperty, Strings.Current["Exit"]),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
                ((InvokePattern)exit.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            });
            samples["ExitCommandReachesMonitorBeforeChildCloses"] = await Until(() => exitRequested && closed && reopenedChild.HasExited);
            return Write(samples.Values.All(value => value));
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(folder, "dashboard-session-error.txt"), error.ToString());
            return Write(false);
        }
        finally { Strings.Current.SetLanguage(language); }

        bool Write(bool passed)
        {
            File.WriteAllText(Path.Combine(folder, "dashboard-session-checks.json"), JsonSerializer.Serialize(new
            { Passed = passed, IsolatedSettings = true, Samples = samples }, new JsonSerializerOptions { WriteIndented = true }));
            return passed;
        }
    }
    private static AutomationElement Find(AutomationElement window, string id) => window.FindFirst(TreeScope.Descendants,
        new PropertyCondition(AutomationElement.AutomationIdProperty, id)) ?? throw new InvalidOperationException($"Missing control: {id}");
    private static async Task<bool> Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(50);
        return condition();
    }
    private sealed class MemoryStore : ISettingsStore
    {
        public AppSettings Settings { get; private set; } = new() { Language = "en", StartWithWindows = false };
        public AppSettings Load() => Settings;
        public void Save(AppSettings settings) => Settings = settings;
    }
    private sealed class FixtureProvider : IBatteryProvider
    {
        public int Reads { get; private set; }
        public Task<IReadOnlyList<BatteryDevice>> GetDevicesAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult<IReadOnlyList<BatteryDevice>>([
                new("session:mouse", "Session test mouse", DeviceKind.Mouse, 74, false, ReadingState.Available, "test", "test")]);
        }
    }
}

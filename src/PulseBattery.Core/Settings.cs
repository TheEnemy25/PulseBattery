namespace PulseBattery.Core;

public enum TaskbarIndicatorSize { Compact, Standard, Large }
public enum TaskbarLayoutPreference { Horizontal, Compact }

public sealed record AppSettings
{
    public int RefreshSeconds { get; init; } = 60;
    public int LowBatteryThreshold { get; init; } = 15;
    public bool LowBatteryNotifications { get; init; } = true;
    public bool ShowTaskbarPanel { get; init; } = true;
    // Legacy JSON fields remain readable; the launcher now lives in the system tray.
    public bool PulseOnLeft { get; init; }
    public TaskbarIndicatorSize TaskbarSize { get; init; } = TaskbarIndicatorSize.Compact;
    public int? TaskbarScalePercent { get; init; }
    public int? TaskbarSpacing { get; init; }
    public TaskbarLayoutPreference TaskbarLayout { get; init; }
    public int? TaskbarCompactScalePercent { get; init; }
    public int? TaskbarCompactSpacing { get; init; }
    public string Language { get; init; } = "uk";
    public Dictionary<string, DeviceKind> DeviceKinds { get; init; } = [];
    public string[] DeviceOrder { get; init; } = [];
    public bool? StartWithWindows { get; init; }
    public string[] HiddenTaskbarDevices { get; init; } = [];

    public AppSettings Validate() => this with
    {
        RefreshSeconds = Math.Clamp(RefreshSeconds, 15, 300),
        LowBatteryThreshold = Math.Clamp(LowBatteryThreshold, 5, 40),
        Language = Language == "en" ? "en" : "uk",
        TaskbarSize = Enum.IsDefined(TaskbarSize) ? TaskbarSize : TaskbarIndicatorSize.Compact,
        TaskbarScalePercent = Math.Clamp(TaskbarScalePercent ?? (TaskbarSize switch
        { TaskbarIndicatorSize.Standard => 120, TaskbarIndicatorSize.Large => 140, _ => 100 }), 80, 150),
        TaskbarSpacing = Math.Clamp(TaskbarSpacing ?? (TaskbarSize switch
        { TaskbarIndicatorSize.Standard => 14, TaskbarIndicatorSize.Large => 16, _ => 10 }), 0, 24),
        TaskbarLayout = Enum.IsDefined(TaskbarLayout) ? TaskbarLayout : TaskbarLayoutPreference.Horizontal,
        TaskbarCompactScalePercent = Math.Clamp(TaskbarCompactScalePercent ?? TaskbarScalePercent ?? (TaskbarSize switch
        { TaskbarIndicatorSize.Standard => 120, TaskbarIndicatorSize.Large => 140, _ => 100 }), 80, 150),
        TaskbarCompactSpacing = Math.Clamp(TaskbarCompactSpacing ?? TaskbarSpacing ?? (TaskbarSize switch
        { TaskbarIndicatorSize.Standard => 14, TaskbarIndicatorSize.Large => 16, _ => 10 }), 0, 24),
        DeviceKinds = (DeviceKinds ?? []).Where(pair => Enum.IsDefined(pair.Value)).ToDictionary(),
        DeviceOrder = (DeviceOrder ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray(),
        HiddenTaskbarDevices = (HiddenTaskbarDevices ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray()
    };
}

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}

public interface IStartupRegistration
{
    void SetEnabled(bool enabled);
}

namespace PulseBattery.Core;

public enum TaskbarIndicatorSize { Compact, Standard, Large }

public sealed record AppSettings
{
    public int RefreshSeconds { get; init; } = 60;
    public int LowBatteryThreshold { get; init; } = 15;
    public bool LowBatteryNotifications { get; init; } = true;
    public bool ShowTaskbarPanel { get; init; } = true;
    public bool PulseOnLeft { get; init; }
    public TaskbarIndicatorSize TaskbarSize { get; init; } = TaskbarIndicatorSize.Compact;
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

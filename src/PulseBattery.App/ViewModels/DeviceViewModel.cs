using System.Windows.Media;
using PulseBattery.Core;
using PulseBattery.App.Localization;

namespace PulseBattery.App.ViewModels;

public sealed class DeviceViewModel(BatteryDevice device, int threshold, DeviceKind? kindOverride = null,
    bool showOnTaskbar = true, Action<string, bool>? setTaskbarVisibility = null) : ObservableObject
{
    private bool _showOnTaskbar = showOnTaskbar;
    public bool ShowOnTaskbar
    {
        get => _showOnTaskbar;
        set { if (Set(ref _showOnTaskbar, value)) setTaskbarVisibility?.Invoke(Device.Id, value); }
    }
    internal void UpdateTaskbarVisibility(bool visible) => Set(ref _showOnTaskbar, visible, nameof(ShowOnTaskbar));
    public BatteryDevice Device { get; } = device;
    public DeviceKind Kind => kindOverride ?? Device.Kind;
    public string Name => Device.Name;
    public string Category => Strings.Current["Kind" + Kind].ToUpper(Strings.Current.Culture);
    public string Level => Device.BatteryPercent?.ToString() ?? "—";
    public string Percent => Device.BatteryPercent.HasValue ? "%" : "";
    public double Progress => Device.BatteryPercent ?? 0;
    public string Connection => Strings.Current.TranslateDetail(Device.Detail);
    public string Source => Device.Source;
    public bool IsCharging => Device.State == ReadingState.Available && Device.IsCharging == true;
    public double TaskbarOpacity => Device.State == ReadingState.Available ? 1 : 0.55;
    public string Status => Device.State switch
    {
        ReadingState.Stale => Strings.Current.Format("LastReading", Device.LastSeen),
        ReadingState.Unavailable => Strings.Current["NoData"],
        _ when Device.IsCharging == true => Strings.Current["Charging"],
        _ when Device.IsLow(threshold) => Strings.Current["ChargeSoon"],
        _ => Strings.Current["OnBattery"]
    };
    public Brush Accent => new SolidColorBrush((Color)ColorConverter.ConvertFromString(Device.State != ReadingState.Available ? "#7F8B84" : Device.IsLow(threshold) ? "#FF9C8B" : "#B7F7CF"));
    public string Badge => Strings.Current[Device.State == ReadingState.Available ? "Connected" : Device.State == ReadingState.Stale ? "Waiting" : "NoResponse"];
}

using System.Collections.ObjectModel;
using PulseBattery.Core;
using PulseBattery.App.Localization;

namespace PulseBattery.App.ViewModels;

public sealed class DashboardViewModel : ObservableObject
{
    private readonly IBatteryMonitor _monitor;
    private readonly ISettingsStore _settingsStore;
    private readonly CancellationToken _lifetime;
    private readonly IStartupRegistration? _startupRegistration;
    private AppSettings _settings;
    private bool _isBusy;
    private bool _settingsOpen;
    private DateTime? _lastRefresh;
    private string _error = "";
    public ObservableCollection<DeviceViewModel> Devices { get; } = [];
    public ObservableCollection<DeviceViewModel> TaskbarDevices { get; } = [];
    public AsyncCommand RefreshCommand { get; }
    public event Action? SettingsChanged;
    public event Action? DevicesChanged;
    public AppSettings Settings => _settings;
    public bool IsBusy { get => _isBusy; private set { Set(ref _isBusy, value); RefreshCommand.Update(); Changed(nameof(RefreshLabel)); } }
    public string RefreshLabel => Strings.Current[IsBusy ? "Reading" : "RefreshButton"];
    public bool SettingsOpen { get => _settingsOpen; set => Set(ref _settingsOpen, value); }
    public string Status => _lastRefresh.HasValue ? Strings.Current.Format("UpdatedAt", _lastRefresh.Value) : Strings.Current["Searching"];
    public string Error { get => Strings.Current.TranslateDetail(_error); private set { Set(ref _error, value); Changed(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;
    public bool IsEmpty => !IsBusy && Devices.Count == 0;
    public string DeviceCount => Strings.Current.Format("DeviceCount", Devices.Count);
    public string Health => Strings.Current[Devices.Any(d => d.Device.IsLow(_settings.LowBatteryThreshold)) ? "HealthLow" : Devices.Any(d => d.Device.State == ReadingState.Available) ? "HealthGood" : "HealthWaiting"];
    public string RefreshHint => Strings.Current.Format("RefreshHint", RefreshSeconds);
    public IReadOnlyList<LanguageChoice> Languages { get; } = [new("uk", "Українська"), new("en", "English")];
    public string Language
    {
        get => _settings.Language;
        set
        {
            var code = value == "en" ? "en" : "uk";
            if (_settings.Language == code) return;
            _settings = _settings with { Language = code };
            Strings.Current.SetLanguage(code); Save(); Changed();
        }
    }
    public int RefreshSeconds { get => _settings.RefreshSeconds; set { _settings = (_settings with { RefreshSeconds = value }).Validate(); Save(); Changed(); Changed(nameof(RefreshHint)); } }
    public int LowBatteryThreshold { get => _settings.LowBatteryThreshold; set { _settings = (_settings with { LowBatteryThreshold = value }).Validate(); Save(); Changed(); RebuildDevices(); } }
    public bool Notifications { get => _settings.LowBatteryNotifications; set { _settings = _settings with { LowBatteryNotifications = value }; Save(); Changed(); } }
    public bool TaskbarPanel { get => _settings.ShowTaskbarPanel; set { _settings = _settings with { ShowTaskbarPanel = value }; Save(); Changed(); } }
    public bool PulseOnLeft
    {
        get => _settings.PulseOnLeft;
        set { if (_settings.PulseOnLeft == value) return; _settings = _settings with { PulseOnLeft = value }; Changed(); Save(); }
    }
    public IReadOnlyList<PulsePositionChoice> PulsePositions =>
        [new(true, Strings.Current["PulsePositionLeft"]), new(false, Strings.Current["PulsePositionRight"])];
    public TaskbarIndicatorSize TaskbarSize
    {
        get => _settings.TaskbarSize;
        set
        {
            var updated = (_settings with { TaskbarSize = value }).Validate();
            if (updated.TaskbarSize == _settings.TaskbarSize) return;
            _settings = updated; Changed(); Changed(nameof(TaskbarAppearance)); Save();
        }
    }
    public TaskbarAppearance TaskbarAppearance => TaskbarAppearance.For(TaskbarSize);
    public IReadOnlyList<TaskbarSizeChoice> TaskbarSizes => Enum.GetValues<TaskbarIndicatorSize>()
        .Select(size => new TaskbarSizeChoice(size, Strings.Current["TaskbarSize" + size])).ToArray();
    public bool StartWithWindows { get => _settings.StartWithWindows == true; set => SetStartupPreference(value); }
    public bool NeedsStartupChoice => _settings.StartWithWindows is null;

    public DashboardViewModel(IBatteryMonitor monitor, ISettingsStore settingsStore, CancellationToken lifetime, IStartupRegistration? startupRegistration = null)
    {
        _monitor = monitor; _settingsStore = settingsStore; _lifetime = lifetime;
        _startupRegistration = startupRegistration;
        _settings = settingsStore.Load();
        Strings.Current.SetLanguage(_settings.Language);
        RefreshCommand = new(RefreshAsync, () => !IsBusy);
        Strings.Current.LanguageChanged += OnLanguageChanged;
    }

    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true; Changed(nameof(IsEmpty));
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var result = await _monitor.ScanAsync(timeout.Token);
            Devices.Clear();
            foreach (var device in DeviceOrdering.Apply(result.Devices, _settings.DeviceOrder)) Devices.Add(CreateDevice(device));
            RefreshTaskbarDevices();
            Error = string.Join(Environment.NewLine, result.Errors);
            _lastRefresh = DateTime.Now; Changed(nameof(Status));
            DevicesChanged?.Invoke();
        }
        catch (OperationCanceledException) { if (!_lifetime.IsCancellationRequested) Error = Strings.Current["Timeout"]; }
        catch (Exception exception) { Error = exception.Message; }
        finally { IsBusy = false; Changed(nameof(IsEmpty)); Changed(nameof(DeviceCount)); Changed(nameof(Health)); }
    }

    private void Save()
    {
        try { _settingsStore.Save(_settings); Error = ""; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Error = Strings.Current.Format("SaveError", exception.Message); }
        SettingsChanged?.Invoke();
    }
    private void RebuildDevices()
    {
        var devices = Devices.Select(d => d.Device).ToArray();
        Devices.Clear(); foreach (var device in DeviceOrdering.Apply(devices, _settings.DeviceOrder)) Devices.Add(CreateDevice(device));
        RefreshTaskbarDevices();
        Changed(nameof(Health)); DevicesChanged?.Invoke();
    }
    private DeviceViewModel CreateDevice(BatteryDevice device) => new(device, LowBatteryThreshold,
        _settings.DeviceKinds.TryGetValue(device.Id, out var kind) ? kind : null,
        !_settings.HiddenTaskbarDevices.Contains(device.Id, StringComparer.Ordinal), SetDeviceTaskbarVisibility);
    public void SetDeviceTaskbarVisibility(string deviceId, bool visible)
    {
        var hidden = _settings.HiddenTaskbarDevices.ToHashSet(StringComparer.Ordinal);
        if (visible ? !hidden.Remove(deviceId) : !hidden.Add(deviceId)) return;
        _settings = _settings with { HiddenTaskbarDevices = hidden.ToArray() };
        foreach (var device in Devices.Where(device => device.Device.Id == deviceId)) device.UpdateTaskbarVisibility(visible);
        RefreshTaskbarDevices(); DevicesChanged?.Invoke(); Save();
    }
    private void RefreshTaskbarDevices()
    {
        var visible = Devices.Where(device => device.ShowOnTaskbar).ToArray();
        for (var index = 0; index < visible.Length; index++)
        {
            if (index < TaskbarDevices.Count && ReferenceEquals(TaskbarDevices[index], visible[index])) continue;
            var existing = TaskbarDevices.IndexOf(visible[index]);
            if (existing >= 0) TaskbarDevices.Move(existing, index); else TaskbarDevices.Insert(index, visible[index]);
        }
        while (TaskbarDevices.Count > visible.Length) TaskbarDevices.RemoveAt(TaskbarDevices.Count - 1);
    }
    public void SetDeviceKind(string deviceId, DeviceKind? kind)
    {
        var kinds = new Dictionary<string, DeviceKind>(_settings.DeviceKinds);
        if (kind is null) kinds.Remove(deviceId); else kinds[deviceId] = kind.Value;
        _settings = _settings with { DeviceKinds = kinds };
        Save(); RebuildDevices();
    }
    public bool MoveDevice(string sourceId, string targetId)
    {
        var ids = Devices.Select(device => device.Device.Id).ToList();
        var from = ids.IndexOf(sourceId);
        var to = ids.IndexOf(targetId);
        if (from < 0 || to < 0 || from == to) return false;
        Devices.Move(from, to);
        RefreshTaskbarDevices();
        _settings = _settings with { DeviceOrder = DeviceOrdering.MergeVisible(_settings.DeviceOrder, Devices.Select(device => device.Device.Id)) };
        Save(); DevicesChanged?.Invoke();
        return true;
    }
    public bool SetStartupPreference(bool enabled)
    {
        if (_settings.StartWithWindows == enabled) return true;
        var registrationChanged = false;
        try
        {
            _startupRegistration?.SetEnabled(enabled);
            registrationChanged = _startupRegistration is not null;
            var updated = _settings with { StartWithWindows = enabled };
            _settingsStore.Save(updated);
            _settings = updated; Error = "";
            Changed(nameof(StartWithWindows)); Changed(nameof(NeedsStartupChoice)); SettingsChanged?.Invoke();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (registrationChanged)
            {
                try { _startupRegistration!.SetEnabled(_settings.StartWithWindows == true); }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
            Error = Strings.Current.Format("StartupSaveError", exception.Message);
            Changed(nameof(StartWithWindows));
            return false;
        }
    }
    public void UpdateStartupExecutable()
    {
        if (_settings.StartWithWindows != true || _startupRegistration is null) return;
        try { _startupRegistration.SetEnabled(true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { Error = Strings.Current.Format("StartupSaveError", exception.Message); }
    }
    private void OnLanguageChanged() { Changed(null); RebuildDevices(); }
}

public sealed record LanguageChoice(string Code, string Name);
public sealed record PulsePositionChoice(bool IsLeft, string Name);
public sealed record TaskbarSizeChoice(TaskbarIndicatorSize Size, string Name);

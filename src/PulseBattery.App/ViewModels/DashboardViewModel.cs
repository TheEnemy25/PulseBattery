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
    internal DateTime? LastRefreshAt => _lastRefresh;
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
    public int TaskbarScalePercent
    {
        get => _settings.TaskbarScalePercent ?? 100;
        set
        {
            var updated = (_settings with { TaskbarScalePercent = value }).Validate();
            if (updated.TaskbarScalePercent == TaskbarScalePercent) return;
            _settings = updated; Changed(); Changed(nameof(TaskbarAppearance)); Save();
        }
    }
    public int TaskbarSpacing
    {
        get => _settings.TaskbarSpacing ?? 10;
        set
        {
            var updated = (_settings with { TaskbarSpacing = value }).Validate();
            if (updated.TaskbarSpacing == TaskbarSpacing) return;
            _settings = updated; Changed(); Changed(nameof(TaskbarAppearance)); Save();
        }
    }
    public TaskbarAppearance TaskbarAppearance => TaskbarAppearance.For(TaskbarScalePercent, TaskbarSpacing);
    public TaskbarAppearance CompactTaskbarAppearance => TaskbarAppearance.For(TaskbarCompactScalePercent, TaskbarCompactSpacing);
    public int TaskbarLayoutMode
    {
        get => _settings.TaskbarLayout == TaskbarLayoutPreference.Compact ? 1 : 0;
        set
        {
            var mode = value == 1 ? TaskbarLayoutPreference.Compact : TaskbarLayoutPreference.Horizontal;
            if (_settings.TaskbarLayout == mode) return;
            _settings = _settings with { TaskbarLayout = mode }; Changed(); Save();
        }
    }
    public int TaskbarCompactScalePercent
    {
        get => _settings.TaskbarCompactScalePercent ?? 100;
        set
        {
            var updated = (_settings with { TaskbarCompactScalePercent = value }).Validate();
            if (updated.TaskbarCompactScalePercent == TaskbarCompactScalePercent) return;
            _settings = updated; Changed(); Changed(nameof(CompactTaskbarAppearance)); Save();
        }
    }
    public int TaskbarCompactSpacing
    {
        get => _settings.TaskbarCompactSpacing ?? 10;
        set
        {
            var updated = (_settings with { TaskbarCompactSpacing = value }).Validate();
            if (updated.TaskbarCompactSpacing == TaskbarCompactSpacing) return;
            _settings = updated; Changed(); Changed(nameof(CompactTaskbarAppearance)); Save();
        }
    }
    public bool StartWithWindows { get => _settings.StartWithWindows == true; set => SetStartupPreference(value); }
    public bool NeedsStartupChoice => _settings.StartWithWindows is null;

    public DashboardViewModel(IBatteryMonitor monitor, ISettingsStore settingsStore, CancellationToken lifetime, IStartupRegistration? startupRegistration = null, Func<Task>? requestRefresh = null)
    {
        _monitor = monitor; _settingsStore = settingsStore; _lifetime = lifetime;
        _startupRegistration = startupRegistration;
        _settings = settingsStore.Load().Validate();
        Strings.Current.SetLanguage(_settings.Language);
        RefreshCommand = new(requestRefresh ?? RefreshAsync, () => !IsBusy);
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
            SynchronizeDevices(result.Devices);
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
        if (SynchronizeDevices(devices)) { Changed(nameof(Health)); DevicesChanged?.Invoke(); }
    }
    private bool SynchronizeDevices(IReadOnlyList<BatteryDevice> readings)
    {
        var existing = Devices.ToDictionary(device => device.Device.Id, StringComparer.Ordinal);
        var ordered = DeviceOrdering.Apply(readings, _settings.DeviceOrder).ToArray();
        var changed = false;
        for (var index = 0; index < ordered.Length; index++)
        {
            var reading = ordered[index];
            if (!existing.TryGetValue(reading.Id, out var device))
            {
                device = CreateDevice(reading); Devices.Insert(index, device); changed = true;
            }
            else
            {
                changed |= device.Update(reading, LowBatteryThreshold,
                    _settings.DeviceKinds.TryGetValue(reading.Id, out var kind) ? kind : null,
                    !_settings.HiddenTaskbarDevices.Contains(reading.Id, StringComparer.Ordinal));
                var position = Devices.IndexOf(device);
                if (position != index) { Devices.Move(position, index); changed = true; }
            }
        }
        while (Devices.Count > ordered.Length) { Devices.RemoveAt(Devices.Count - 1); changed = true; }
        RefreshTaskbarDevices();
        return changed;
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
    private void OnLanguageChanged()
    {
        Changed(null);
        foreach (var device in Devices) device.RefreshLocalization();
    }
    internal void ApplyDashboardSettings(AppSettings settings)
    {
        settings = settings.Validate();
        if (_settings.StartWithWindows != settings.StartWithWindows && settings.StartWithWindows.HasValue &&
            !SetStartupPreference(settings.StartWithWindows.Value)) return;
        var previous = _settings;
        _settings = settings;
        Strings.Current.SetLanguage(settings.Language);
        NotifySettingsChanges(previous); RebuildDevices(); Save();
    }
    internal void ApplyDashboardState(AppSettings settings, IReadOnlyList<BatteryDevice> devices, bool busy, string error, DateTime? lastRefresh)
    {
        var previous = _settings;
        _settings = settings.Validate();
        Strings.Current.SetLanguage(_settings.Language);
        var changed = SynchronizeDevices(devices);
        _lastRefresh = lastRefresh;
        Error = error; IsBusy = busy;
        NotifySettingsChanges(previous);
        Changed(nameof(Status)); Changed(nameof(IsEmpty)); Changed(nameof(DeviceCount)); Changed(nameof(Health));
        if (changed) DevicesChanged?.Invoke();
    }
    private void NotifySettingsChanges(AppSettings previous)
    {
        if (previous.Language != _settings.Language) Changed(nameof(Language));
        if (previous.RefreshSeconds != _settings.RefreshSeconds) { Changed(nameof(RefreshSeconds)); Changed(nameof(RefreshHint)); }
        if (previous.LowBatteryThreshold != _settings.LowBatteryThreshold) Changed(nameof(LowBatteryThreshold));
        if (previous.LowBatteryNotifications != _settings.LowBatteryNotifications) Changed(nameof(Notifications));
        if (previous.ShowTaskbarPanel != _settings.ShowTaskbarPanel) Changed(nameof(TaskbarPanel));
        if (previous.StartWithWindows != _settings.StartWithWindows) { Changed(nameof(StartWithWindows)); Changed(nameof(NeedsStartupChoice)); }
        if (previous.TaskbarScalePercent != _settings.TaskbarScalePercent) Changed(nameof(TaskbarScalePercent));
        if (previous.TaskbarSpacing != _settings.TaskbarSpacing) Changed(nameof(TaskbarSpacing));
        if (previous.TaskbarLayout != _settings.TaskbarLayout) Changed(nameof(TaskbarLayoutMode));
        if (previous.TaskbarCompactScalePercent != _settings.TaskbarCompactScalePercent) Changed(nameof(TaskbarCompactScalePercent));
        if (previous.TaskbarCompactSpacing != _settings.TaskbarCompactSpacing) Changed(nameof(TaskbarCompactSpacing));
        if (previous.TaskbarCompactScalePercent != _settings.TaskbarCompactScalePercent || previous.TaskbarCompactSpacing != _settings.TaskbarCompactSpacing)
            Changed(nameof(CompactTaskbarAppearance));
        if (previous.TaskbarScalePercent != _settings.TaskbarScalePercent || previous.TaskbarSpacing != _settings.TaskbarSpacing)
            Changed(nameof(TaskbarAppearance));
    }
}

public sealed record LanguageChoice(string Code, string Name);

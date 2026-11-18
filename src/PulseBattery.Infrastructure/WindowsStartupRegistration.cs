using Microsoft.Win32;
using PulseBattery.Core;

namespace PulseBattery.Infrastructure;

public sealed class WindowsStartupRegistration(string executablePath) : IStartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PulseBattery";
    public const string InstallerPreferencesKey = @"Software\PulseBattery\InstallerStartup";

    public void ApplyInstallerPreference(ISettingsStore settingsStore)
    {
        // Setup records explicit consent for this executable, including a declined choice.
        // Consume it only after saving, so a failed save can be retried on the next launch.
        var path = Path.GetFullPath(executablePath);
        using var key = Registry.CurrentUser.OpenSubKey(InstallerPreferencesKey, writable: true);
        if (key?.GetValue(path) is not int choice || choice is not (0 or 1)) return;
        settingsStore.Save(settingsStore.Load() with { StartWithWindows = choice == 1 });
        key.DeleteValue(path, throwOnMissingValue: false);
    }

    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            using var readOnly = Registry.CurrentUser.OpenSubKey(RunKey);
            if (readOnly?.GetValue(ValueName) is null) return;
            using var existing = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            existing?.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new IOException("Cannot open the current user's startup settings.");
        key.SetValue(ValueName, BuildCommand(executablePath), RegistryValueKind.String);
    }

    public static string BuildCommand(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var path = Path.GetFullPath(executablePath);
        if (path.IndexOfAny(['"', '\r', '\n']) >= 0 || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Startup requires a valid executable path.", nameof(executablePath));
        var command = $"\"{path}\" --autostart";
        if (command.Length > 260) throw new IOException("The executable path is too long for Windows startup.");
        return command;
    }
}

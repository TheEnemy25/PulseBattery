using System.Text.Json;
using PulseBattery.Core;

namespace PulseBattery.Infrastructure;

public sealed class JsonSettingsStore(string? filePath = null) : ISettingsStore
{
    private readonly string _path = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseBattery", "settings.json");
    public AppSettings Load()
    {
        try { return (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new()).Validate(); }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings.Validate(), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, _path, true);
    }
}

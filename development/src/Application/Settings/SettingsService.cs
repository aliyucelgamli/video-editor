using System.Text.Json;

namespace VideoEditor.Application.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as human-readable JSON. Fails
/// soft in both directions: a missing or corrupt file loads as defaults, and
/// a failed save never crashes the app (settings are conveniences, not data).
/// </summary>
public class SettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _path;

    /// <summary>Settings live in <paramref name="directory"/>/settings.json.</summary>
    public SettingsService(string directory) => _path = Path.Combine(directory, "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options)
                   ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Temp file + move: a crash mid-save cannot corrupt the settings.
            var tempPath = _path + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, Options));
            File.Move(tempPath, _path, overwrite: true);
        }
        catch
        {
            // Best effort only.
        }
    }
}

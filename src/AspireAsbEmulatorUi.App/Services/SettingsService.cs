using AspireAsbEmulatorUi.App.Models;
using System.Text.Json;

namespace AspireAsbEmulatorUi.App.Services;

public class SettingsService
{
    private const string StorageKey = "aspire-asb-emulator-settings";
    private AppSettings _settings;

    public SettingsService()
    {
        _settings = new AppSettings();

        // Try to load default settings from a settings.json file located in the app base directory
        try
        {
            var candidates = new List<string>();
            // App base directory
            var baseDir = AppContext.BaseDirectory ?? Directory.GetCurrentDirectory();
            candidates.Add(Path.Combine(baseDir, "settings.json"));
            // Current directory
            candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "settings.json"));
            // Probe a couple of likely relative paths (when running from source)
            candidates.Add(Path.Combine(baseDir, "..", "settings.json"));
            candidates.Add(Path.Combine(baseDir, "..", "..", "settings.json"));

            var file = candidates.FirstOrDefault(File.Exists);
            if (!string.IsNullOrEmpty(file))
            {
                var json = File.ReadAllText(file);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null)
                {
                    _settings = settings;
                }
            }
        }
        catch
        {
            // Ignore errors and fall back to defaults
        }
    }

    public AppSettings GetSettings()
    {
        return _settings;
    }

    public void UpdateSettings(AppSettings settings)
    {
        _settings = settings;
    }

    public string ExportSettingsAsJson()
    {
        return JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
    }

    public void ImportSettingsFromJson(string json)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(json);
            if (settings != null)
            {
                _settings = settings;
            }
        }
        catch
        {
            // Invalid JSON, keep existing settings
        }
    }
}

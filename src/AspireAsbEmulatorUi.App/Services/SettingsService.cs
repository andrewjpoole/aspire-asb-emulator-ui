using AspireAsbEmulatorUi.App.Models;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using AspireAsbEmulatorUi.Models;

namespace AspireAsbEmulatorUi.App.Services;

public class SettingsService
{
    private const string StorageKey = "aspire-asb-emulator-settings";
    private Settings _settings;
    private readonly IConfiguration _configuration;

    public SettingsService(IConfiguration configuration)
    {
        _configuration = configuration;
        _settings = new Settings();

        // First, try to load from settings override (passed from Aspire AppHost)
        var settingsOverride = _configuration["AsbEmulatorUi__SettingsOverride"]
                              ?? _configuration["ASBEMULATORUI__SETTINGSOVERRIDE"];

        if (!string.IsNullOrWhiteSpace(settingsOverride))
        {
            try
            {
                var deserializedSettings = JsonSerializer.Deserialize<Settings>(settingsOverride,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (deserializedSettings != null)
                {
                    _settings = deserializedSettings;
                    return; // Use override and skip file loading
                }
            }
            catch
            {
                // Ignore and fall through to file loading
            }
        }

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
                var settings = JsonSerializer.Deserialize<Settings>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
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

    public Settings GetSettings()
    {
        return _settings;
    }

    public void UpdateSettings(Settings settings)
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
            var settings = JsonSerializer.Deserialize<Settings>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
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

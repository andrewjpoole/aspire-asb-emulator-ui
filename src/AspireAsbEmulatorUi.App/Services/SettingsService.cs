using System.Text.Json;
using AspireAsbEmulatorUi.Models;
using Microsoft.JSInterop;

namespace AspireAsbEmulatorUi.App.Services;

public class SettingsService
{
    private const string StorageKey = "aspire-asb-emulator-settings";
    private Settings _settings;
    private readonly IConfiguration _configuration;
    private readonly IJSRuntime _js;
    private readonly ILogger<SettingsService> _logger;

    public SettingsService(IConfiguration configuration, IJSRuntime js, ILogger<SettingsService> logger)
    {
        _configuration = configuration;
        _js = js;
        _logger = logger;
        _settings = new Settings();
        LoadDefaults();
    }

    private void LoadDefaults()
    {
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse settings override from configuration.");
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings from settings.json file.");
            // Ignore errors and fall back to defaults
        }
    }

    public Settings GetSettings()
    {
        return _settings;
    }

    public event Action? OnSettingsChanged;

    public void UpdateSettings(Settings settings)
    {
        settings.LastUpdated = DateTime.UtcNow;
        _settings = settings;
        OnSettingsChanged?.Invoke();
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
                OnSettingsChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import settings from JSON.");
            // Invalid JSON, keep existing settings
        }
    }

    public async Task LoadFromStorageAsync()
    {
        try
        {
            var json = await _js.InvokeAsync<string>("localStorage.getItem", StorageKey);
            if (!string.IsNullOrEmpty(json))
            {
                var settings = JsonSerializer.Deserialize<Settings>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (settings != null)
                {
                    _logger.LogInformation("Loaded settings from storage. Storage Timestamp: {StorageTime}, Current Timestamp: {CurrentTime}", 
                        settings.LastUpdated, _settings.LastUpdated);

                    bool shouldUseLocalStorage = false;

                    if (_settings.LastUpdated == null)
                    {
                        _logger.LogInformation("Current settings (from file/defaults) have no timestamp. Preferring local storage.");
                        shouldUseLocalStorage = true;
                    }
                    else if (settings.LastUpdated.HasValue && settings.LastUpdated > _settings.LastUpdated)
                    {
                        _logger.LogInformation("Local storage settings are newer. Applying local storage.");
                        shouldUseLocalStorage = true;
                    }
                    else
                    {
                        _logger.LogInformation("Local storage settings are older or same. Keeping current settings.");
                    }

                    if (shouldUseLocalStorage)
                    {
                        _settings = settings;
                        OnSettingsChanged?.Invoke();
                    }
                }
            }
            else
            {
                _logger.LogInformation("No settings found in local storage.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load settings from local storage (this is expected during server-side prerendering).");
            // Ignore errors (e.g. JS not available)
        }
    }

    public async Task SaveToStorageAsync()
    {
        try
        {
            var json = ExportSettingsAsJson();
            await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings to local storage.");
            // Ignore errors
        }
    }

    public async Task ClearStorageAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("localStorage.removeItem", StorageKey);
            LoadDefaults();
            OnSettingsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear local storage.");
        }
    }
}

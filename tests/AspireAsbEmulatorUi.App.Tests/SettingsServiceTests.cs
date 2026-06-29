using AspireAsbEmulatorUi.App.Services;
using AspireAsbEmulatorUi.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using NSubstitute;
using System.Text.Json;

namespace AspireAsbEmulatorUi.App.Tests;

public class SettingsServiceTests
{
    [Test]
    public async Task SettingsService_Initializes_WithSettingsOverride()
    {
        // Create unique settings
        var settings = new Settings()
        {
            CannedMessages = new()
            {
                ["test-topic"] = new()
                {
                    ["MyTestMessage"] = new()
                    {
                        Body = """
                        {
                            "Id": 1,
                            "Name": "Test Name",
                            "IsActive": true
                        }
                        """
                    }
                }
            }
        };

        // Set environment variable
        var serializedSettings = JsonSerializer.Serialize(settings);
        Environment.SetEnvironmentVariable("AsbEmulatorUi__SettingsOverride", serializedSettings);

        // Build configuration object
        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();

        var settingsService = new SettingsService(
            config,
            Substitute.For<IJSRuntime>(),
            NullLogger<SettingsService>.Instance);

        var result = settingsService.GetSettings();

        await Assert.That(result).IsEquivalentTo(settings);
    }
}

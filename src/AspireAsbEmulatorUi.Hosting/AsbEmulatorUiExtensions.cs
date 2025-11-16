using Aspire.AsbEmulatorUi.Integration;
using Aspire.Hosting.Azure;
using AspireAsbEmulatorUi.App.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding ASB Emulator UI to an Aspire AppHost
/// </summary>
public static class AsbEmulatorUiResourceExtensions
{
    /// <summary>
    /// Adds the ASB Emulator UI to the application, automatically wiring it to an Azure Service Bus emulator resource
    /// </summary>
    /// <param name="builder">The distributed application builder</param>
    /// <param name="name">The name for the UI resource</param>
    /// <param name="serviceBusResource">The Azure Service Bus emulator resource to connect to</param>
    /// <param name="configureOptions">Optional configuration for the UI</param>
    /// <returns>A resource builder for the UI project</returns>
    public static IResourceBuilder<AsbEmulatorUiResource> AddAsbEmulatorUi(
        this IDistributedApplicationBuilder builder,
        string name,
        IResourceBuilder<AzureServiceBusResource> serviceBusResource,
        int httpPort = 8000)
    {       
        var asbEmulatorUiResourceBuilder = builder.AddResource(new AsbEmulatorUiResource(name))
                    .WithImage("andrewjpoole/aspireasbemulatorui")
                    .WithImageRegistry("docker.io")
                    .WithHttpEndpoint(port: httpPort, targetPort: 8080)
                    .WithReference(serviceBusResource)  // This passes the connection string!
                    .WaitFor(serviceBusResource)
                    .ExcludeFromManifest()
                    .WithEnvironment(async (context) =>
                    {
                        await AsbEmulatorUiResourceExtensions.WireUpToAsbEmulator(context, serviceBusResource);
                    }); // Not using builder extension pattern here to enable easier local testing when resource will be a local ProjectResource rather than a AsbEmulatorUiResource.
        return asbEmulatorUiResourceBuilder;
    }

    public static async Task WireUpToAsbEmulator(EnvironmentCallbackContext context, IResourceBuilder<AzureServiceBusResource> serviceBusResource) 
    {
        // No runtime environment configuration when publishing the app
        if (context.ExecutionContext.IsPublishMode)
            return;

        var sbResource = serviceBusResource.Resource;

        // Expose the resource name for building connection strings
        context.EnvironmentVariables["asb-resource-name"] = sbResource.Name;

        // Find the SQL container that backs the emulator and expose its port
        var sqlAsbContainerResource = serviceBusResource.ApplicationBuilder.Resources.SingleOrDefault(r => r.Name == $"{sbResource.Name}-mssql")
            ?? throw new Exception($"Unable to find ASB emulator SQL container with name {sbResource.Name}-mssql");

        if (!sqlAsbContainerResource.TryGetUrls(out var urls) || urls == null || !urls.Any())
            throw new Exception("Unable to get any SQL endpoint URLs from ASB emulator resource.");

        var firstUrl = urls.First();
        var sqlPort = firstUrl.Endpoint?.Port
            ?? throw new Exception("Unable to get SQL endpoint port from ASB emulator resource.");

        // Expose the port that the ASB emulator's MS SQL backend is running on
        context.EnvironmentVariables["asb-sql-port"] = sqlPort.ToString();

        // Process container environment variables to extract the SQL password
        await sqlAsbContainerResource.ProcessEnvironmentVariableValuesAsync(
            context.ExecutionContext,
            async (key, unprocessedValue, processedValue, exception) =>
            {
                if (key != "MSSQL_SA_PASSWORD")
                    return;

                if (string.IsNullOrEmpty(processedValue))
                {
                    context.Logger.LogError("MSSQL_SA_PASSWORD environment variable returned null or empty value.");
                    return;
                }

                context.EnvironmentVariables["asb-sql-password"] = processedValue;
            },
            context.Logger,
            CancellationToken.None);        
    }    

    /// <summary>
    /// Adds canned messages for multiple entities to the ASB Emulator UI for integration testing
    /// </summary>
    /// <param name="builder">The resource builder</param>
    /// <param name="entitiesWithScenarios">Dictionary of entity names to their canned message scenarios</param>
    /// <returns>The resource builder for chaining</returns>
    /// <exception cref="InvalidOperationException">Thrown when settings have already been overridden via WithOverridenSettingsFile</exception>
    public static IResourceBuilder<T> WithCannedMessages<T>(
        this IResourceBuilder<T> builder,
        Dictionary<string, Dictionary<string, CannedMessage>> entitiesWithScenarios)
        where T : IResourceWithEnvironment
    {
        return builder.WithEnvironment(context =>
        {
            if (context.ExecutionContext.IsPublishMode)
                return;

            // Check if settings have been overridden from a file
            if (context.EnvironmentVariables.TryGetValue("AsbEmulatorUi__SettingsOverride__Source", out var source) 
                && source?.ToString() == "File")
            {
                throw new InvalidOperationException(
                    "Settings have already been overridden via WithOverridenSettingsFile(). " +
                    "Cannot use WithCannedMessages() after settings file has been provided. " +
                    "Either use WithOverridenSettingsFile() OR WithCannedMessages(), not both.");
            }

            // Check if we already have settings override
            var existingSettings = new Settings();

            if (context.EnvironmentVariables.TryGetValue("AsbEmulatorUi__SettingsOverride", out var existing))
            {
                try
                {
                    var existingJson = existing?.ToString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(existingJson))
                    {
                        var deserialized = JsonSerializer.Deserialize<Settings>(existingJson,
                            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                        if (deserialized != null)
                            existingSettings = deserialized;
                    }
                }
                catch
                {
                    // Ignore deserialization errors
                }
            }

            // Add or update canned messages for all entities
            foreach (var entity in entitiesWithScenarios)
            {
                existingSettings.CannedMessages[entity.Key] = entity.Value;
            }

            // Serialize and update
            var settingsJson = JsonSerializer.Serialize(existingSettings, new JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            context.EnvironmentVariables["AsbEmulatorUi__SettingsOverride"] = settingsJson;
            context.EnvironmentVariables["AsbEmulatorUi__SettingsOverride__Source"] = "CannedMessages";
        });
    }

    /// <summary>
    /// Provides a custom settings file path to override the default settings
    /// </summary>
    /// <param name="builder">The resource builder</param>
    /// <param name="settingsFilePath">The path to the settings JSON file</param>
    /// <returns>The resource builder for chaining</returns>
    /// <exception cref="InvalidOperationException">Thrown when settings have already been overridden via WithCannedMessages</exception>
    public static IResourceBuilder<T> WithOverridenSettingsFile<T>(
        this IResourceBuilder<T> builder,
        string settingsFilePath)
        where T : IResourceWithEnvironment
    {
        return builder.WithEnvironment(context =>
        {
            if (context.ExecutionContext.IsPublishMode)
                return;

            // Check if settings have been overridden via WithCannedMessages
            if (context.EnvironmentVariables.TryGetValue("AsbEmulatorUi__SettingsOverride__Source", out var source) 
                && source?.ToString() == "CannedMessages")
            {
                throw new InvalidOperationException(
                    "Settings have already been overridden via WithCannedMessages(). " +
                    "Cannot use WithOverridenSettingsFile() after canned messages have been configured. " +
                    "Either use WithOverridenSettingsFile() OR WithCannedMessages(), not both.");
            }

            if (string.IsNullOrWhiteSpace(settingsFilePath))
            {
                context.Logger.LogWarning("Settings file path is null or empty, skipping settings override.");
                return;
            }

            if (!File.Exists(settingsFilePath))
            {
                context.Logger.LogError("Settings file not found at path: {SettingsFilePath}", settingsFilePath);
                return;
            }

            try
            {
                var settingsJson = File.ReadAllText(settingsFilePath);
                
                // Validate JSON by attempting to deserialize
                var settings = JsonSerializer.Deserialize<Settings>(settingsJson,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

                if (settings == null)
                {
                    context.Logger.LogError("Failed to deserialize settings from file: {SettingsFilePath}", settingsFilePath);
                    return;
                }

                context.EnvironmentVariables["AsbEmulatorUi__SettingsOverride"] = settingsJson;
                context.EnvironmentVariables["AsbEmulatorUi__SettingsOverride__Source"] = "File";
                context.Logger.LogInformation("Settings override loaded from: {SettingsFilePath}", settingsFilePath);
            }
            catch (Exception ex)
            {
                context.Logger.LogError(ex, "Error reading or parsing settings file: {SettingsFilePath}", settingsFilePath);
            }
        });
    }
}

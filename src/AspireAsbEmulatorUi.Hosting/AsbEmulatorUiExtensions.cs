using Aspire.AsbEmulatorUi.Integration;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using AspireAsbEmulatorUi.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Reflection;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding ASB Emulator UI to an Aspire AppHost
/// </summary>
public static class AsbEmulatorUiResourceExtensions
{
    /// <summary>
    /// Adds the ASB Emulator UI to the Azure Service Bus emulator resource, displaying an "Explorer UI" link on the emulator resource in the Aspire dashboard
    /// </summary>
    /// <param name="builder">The resource builder for the Azure Service Bus emulator</param>
    /// <param name="httpPort">The HTTP port for the UI (default: 8000)</param>
    /// <returns>The resource builder for chaining</returns>
    public static IResourceBuilder<AzureServiceBusEmulatorResource> WithUi(
        this IResourceBuilder<AzureServiceBusEmulatorResource> builder,
        int httpPort = 8000)
    {
        var field = typeof(AzureServiceBusEmulatorResource)
            .GetField("_innerResource", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "Unable to find '_innerResource' field on AzureServiceBusEmulatorResource. " +
                "The internal API may have changed in a newer version of the Aspire.Hosting.Azure.ServiceBus package.");

        var innerResource = (AzureServiceBusResource)(field.GetValue(builder.Resource)
            ?? throw new InvalidOperationException(
                "The '_innerResource' field on AzureServiceBusEmulatorResource returned null."));

        var serviceBusBuilder = builder.ApplicationBuilder.CreateResourceBuilder(innerResource);

        var emulatorUi = builder.ApplicationBuilder.AddAsbEmulatorUi(
            $"{builder.Resource.Name}-asb-ui", serviceBusBuilder, httpPort: httpPort);

        emulatorUi.OnInitializeResource(async (resource, evt, ct) =>
        {
            await evt.Notifications.PublishUpdateAsync(resource, s => s with
            {
                IsHidden = true
            });
        });

        builder.WithUrls(context =>
        {
            context.Urls.Add(new ResourceUrlAnnotation
            {
                Url = emulatorUi.GetEndpoint("http").Url,
                DisplayText = "Explorer UI"
            });
        });

        return builder;
    }

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
        int httpPort = 8000,
        string? defaultImageTag = "1.0.2")
    {       
        // Determine an image tag from the hosting assembly's version information.
        // Prefer AssemblyInformationalVersion (maps to <Version> in the csproj),
        // then AssemblyName.Version, then the provided default, then 'latest'.
        string? assemblyVersion = null;
        try
        {
            var asm = typeof(AsbEmulatorUiResource).Assembly;
            assemblyVersion = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? asm.GetName().Version?.ToString();
            if (!string.IsNullOrEmpty(assemblyVersion))
            {
                // Remove build metadata ("+...") and pre-release suffix ("-...") to get core semver
                var v = assemblyVersion;
                var plus = v.IndexOf('+');
                if (plus >= 0) v = v.Substring(0, plus);
                var dash = v.IndexOf('-');
                if (dash >= 0) v = v.Substring(0, dash);
                assemblyVersion = v.Trim();
            }
        }
        catch
        {
            assemblyVersion = null;
        }

        var imageTag = assemblyVersion ?? defaultImageTag ?? "latest";

        var asbEmulatorUiResourceBuilder = builder.AddResource(new AsbEmulatorUiResource(name))
            .WithImage("andrewjpoole/aspireasbemulatorui")
            .WithImageTag(imageTag)
            .WithImageRegistry("docker.io")
            .WithHttpEndpoint(port: httpPort, targetPort: 8080)
            .WithReference(serviceBusResource)
            .WaitFor(serviceBusResource)
            .WithLifetime(ContainerLifetime.Persistent) // This tells Aspire to add this container to a persistent network.
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

        // Find the SQL container that backs the emulator and expose its port
        var sqlAsbContainerResource = serviceBusResource.ApplicationBuilder.Resources.SingleOrDefault(r => r.Name == $"{sbResource.Name}-mssql")
            ?? throw new Exception($"Unable to find ASB emulator SQL container with name {sbResource.Name}-mssql");

        context.EnvironmentVariables["asb-resource-name"] = sbResource.Name;

        if (!sqlAsbContainerResource.TryGetUrls(out var urls) || urls == null || !urls.Any())
            throw new Exception("Unable to get any SQL endpoint URLs from ASB emulator resource.");

        var firstUrl = urls.First();
        var sqlPort = firstUrl.Endpoint?.Port
            ?? throw new Exception("Unable to get SQL endpoint port from ASB emulator resource.");

        // Expose the port and host that the ASB emulator's MS SQL backend is running on
        context.EnvironmentVariables["asb-sql-port"] = sqlPort.ToString();       

        // Process container environment variables to extract the SQL password
        await sbResource.ProcessEnvironmentVariableValuesAsync(
            context.ExecutionContext,
            async (key, unprocessedValue, processedValue, exception) =>
            {
                // Capture SQL password
                if (key == "MSSQL_SA_PASSWORD")
                {
                    if (string.IsNullOrEmpty(processedValue))
                    {
                        context.Logger.LogError("MSSQL_SA_PASSWORD environment variable returned null or empty value.");
                    }
                    else
                    {
                        context.EnvironmentVariables["asb-sql-password"] = processedValue;
                    }
                    return;
                }

                // Capture raw SQL server identifier if present and expose to UI as ASB_EMULATOR_SQLSERVER
                if (key == "SQL_SERVER")
                {
                    if (string.IsNullOrEmpty(processedValue) == false)
                    {
                        context.EnvironmentVariables["asb-emulator-sqlserver"] = processedValue;
                        context.Logger.LogInformation("Exposed SQL_SERVER to UI as asb-emulator-sqlserver: {SqlServer}", processedValue);
                    }
                    return;
                }
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

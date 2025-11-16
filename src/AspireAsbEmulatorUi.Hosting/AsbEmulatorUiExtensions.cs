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
        int httpPort = 8000)//,
        //Action<AsbEmulatorUiOptions>? configureOptions = null)
    {
        //var options = new AsbEmulatorUiOptions();
        //configureOptions?.Invoke(options);

        // Add the project resource
        //var projectResource = builder.AddProject<Projects.AspireAsbEmulatorUi_App>(name)
        //    .WithReference(serviceBusResource)
        //    .WaitFor(serviceBusResource)
        //    .ExcludeFromManifest();

        var asbEmulatorUiResourceBuilder = builder.AddResource(new AsbEmulatorUiResource(name))
                    .WithImage("andrewjpoole/aspireasbemulatorui")
                    .WithImageRegistry("docker.io")
                    .WithHttpEndpoint(port: httpPort, targetPort: 8080);

        // Configure environment variables to connect to the ASB emulator
        asbEmulatorUiResourceBuilder.WithEnvironment(async (context) =>
        {
            // No runtime environment configuration when publishing the app
            if (context.ExecutionContext.IsPublishMode)
                return;

            var sbResource = serviceBusResource.Resource;

            // Expose the resource name for building connection strings
            context.EnvironmentVariables["asb-resource-name"] = sbResource.Name;

            // Find the SQL container that backs the emulator and expose its port
            var sqlAsbContainerResource = builder.Resources.SingleOrDefault(r => r.Name == $"{sbResource.Name}-mssql")
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

            // If settings are configured, serialize and pass as environment variable
            //if (options.ConfigureSettings != null)
            //{
            //    var settings = new Settings();
            //    options.ConfigureSettings(settings);

            //    var settingsJson = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            //    {
            //        WriteIndented = false,
            //        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            //    });

            //    context.EnvironmentVariables["AsbEmulatorUi__SettingsOverride"] = settingsJson;
            //}
        });

        return asbEmulatorUiResourceBuilder;
    }

    /// <summary>
    /// Adds canned messages to the ASB Emulator UI for integration testing
    /// </summary>
    public static IResourceBuilder<ProjectResource> WithCannedMessages(
        this IResourceBuilder<ProjectResource> builder,
        string entityName,
        Dictionary<string, CannedMessage> scenarios)
    {
        return builder.WithEnvironment(context =>
        {
            if (context.ExecutionContext.IsPublishMode)
                return;

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

            // Add or update canned messages
            existingSettings.CannedMessages[entityName] = scenarios;

            // Serialize and update
            var settingsJson = JsonSerializer.Serialize(existingSettings, new JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            context.EnvironmentVariables["AsbEmulatorUi__SettingsOverride"] = settingsJson;
        });
    }

    /// <summary>
    /// Enables the integration test API for programmatic message sending
    /// </summary>
    public static IResourceBuilder<ProjectResource> WithIntegrationTestApi(
        this IResourceBuilder<ProjectResource> builder,
        bool enabled = true)
    {
        return builder.WithEnvironment("AsbEmulatorUi__EnableIntegrationTestApi", enabled.ToString());
    }
}

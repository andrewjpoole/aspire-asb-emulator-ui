using Aspire.Hosting.Azure;
using Microsoft.Extensions.Logging;
using Projects;

var builder = DistributedApplication.CreateBuilder(args);

// Add Azure Service Bus and configure it to run with the emulator
var serviceBus = builder
    .AddAzureServiceBus("myservicebus")
    .RunAsEmulator(c => c.WithLifetime(ContainerLifetime.Persistent));

serviceBus.AddServiceBusQueue("queue-one");
serviceBus.AddServiceBusQueue("queue-two");
serviceBus.AddServiceBusQueue("queue-three");
serviceBus.AddServiceBusQueue("topic-sub1-fwd");
serviceBus.AddServiceBusTopic("topic-one-with-a-very-very-very-very-very-very-very-long-name")
    .AddServiceBusSubscription("sub1-dummy-app");
var topic2 = serviceBus.AddServiceBusTopic("topic-two");
topic2.AddServiceBusSubscription("topic2-sub1-dummy-app");
topic2.AddServiceBusSubscription("topic2-sub2-dummy-app");

var topic = serviceBus.AddServiceBusTopic("topic");
topic.AddServiceBusSubscription("sub1")
     .WithProperties(subscription =>
     {
         subscription.MaxDeliveryCount = 10;
         // CorrelationFilterRules supported but SqlFilters are not yet supported in Aspire.
         // If SqlFilters are needed, supply the emulator config json file with the required filters.
         subscription.ForwardTo = "topic-sub1-fwd";
     });


var apiService = builder.AddProject<AspireAsbEmulatorUi_App>("asb-ui")
    .WithReference(serviceBus)
    .WaitFor(serviceBus)
    .ExcludeFromManifest()
    .WithEnvironment(async (context) =>
    {
        // No runtime environment configuration when publishing the app.
        if (context.ExecutionContext.IsPublishMode)
            return;

        // Find the ASB emulator resource
        var serviceBusResource = builder.Resources.OfType<AzureServiceBusResource>().SingleOrDefault()
            ?? throw new Exception("Unable to find resource of type AzureServiceBusResource");

        // Expose the resource name, this is used to build a connection string in the app.
        context.EnvironmentVariables["asb-resource-name"] = serviceBusResource.Name;

        // Find the SQL container that backs the emulator and expose its port
        var sqlAsbContainerResource = builder.Resources.SingleOrDefault(r => r.Name == $"{serviceBusResource.Name}-mssql")
            ?? throw new Exception($"Unable to find ASB emulator resource with name {serviceBusResource.Name}-mssql");

        if (!sqlAsbContainerResource.TryGetUrls(out var urls) || urls == null || !urls.Any())
            throw new Exception("Unable to get any SQL endpoint URLs from ASB emulator resource.");

        var firstUrl = urls.First();
        var sqlPort = firstUrl.Endpoint?.Port
            ?? throw new Exception("Unable to get SQL endpoint port from ASB emulator resource.");

        // Expose the port that the ASB emulator's MS SQK backend is running on, which changes everytime Aspire is run, this is used to build a connection string in the app.
        context.EnvironmentVariables["asb-sql-port"] = sqlPort;

        // Process container environment variables to extract the SQL password parameter (if present)
        await sqlAsbContainerResource.ProcessEnvironmentVariableValuesAsync(
            context.ExecutionContext,
            async (key, unprocessedValue, processedValue, exception) =>
            {
                if (key != "MSSQL_SA_PASSWORD")
                    return;

                if(string.IsNullOrEmpty(processedValue))
                {
                    context.Logger.LogError("MSSQL_SA_PASSWORD environment variable returned null or empty value when resolving ASB emulator SQL password.");
                    return;
                }

                context.EnvironmentVariables["asb-sql-password"] = processedValue;
            },
            context.Logger,
            CancellationToken.None);
    });

builder.Build().Run();

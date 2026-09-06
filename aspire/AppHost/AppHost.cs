
using AspireAsbEmulatorUi.Models;

var builder = DistributedApplication.CreateBuilder(args);

// Uncomment to test .WithCannedMressages(cannedMessages));
// var cannedMessages = new Dictionary<string, Dictionary<string, CannedMessage>>
// {
//     ["queue-one"] = new()
//     {
//         ["scenario1"] = new CannedMessage
//         {
//             ContentType = "application/json",
//             Body = "{\"canned meessages test\": \"data\"}"
//         }
//     }
// };

// Add Azure Service Bus and configure it to run with the emulator
var serviceBus = builder
    .AddAzureServiceBus("myservicebus")
    .RunAsEmulator(c => c.WithLifetime(ContainerLifetime.Persistent)
        .WithUi(hideEmulatorResourceinAspireDashboard: false)
            .WithOverridenSettingsFile("settings.json"));

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

// uncomment to test UI as a local project resource, rather than pulll from docker etc, for testing new features before merge etc.
// var asbEmulatorUiResourceBuilder = builder.AddProject<Projects.AspireAsbEmulatorUi_App>("asb-ui")
//     .WithReference(serviceBus)
//     .WaitFor(serviceBus)
//     .ExcludeFromManifest()
//     .WithEnvironment(async (context) =>
//     {
//         // Can't use builder extension pattern as this will be a local ProjectResource rather than a AsbEmulatorUiResource.
//         await AsbEmulatorUiResourceExtensions.WireUpToAsbEmulator(context, serviceBus);
//     }); 


// Add MessageHandler console app to process messages from queue-one
builder.AddProject<Projects.MessageHandler>("message-handler")
    .WithReference(serviceBus)
    .WaitFor(serviceBus);

builder.Build().Run();

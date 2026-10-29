
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
serviceBus.AddServiceBusQueue("session-queue")
    .WithProperties(queue => queue.RequiresSession = true);
serviceBus.AddServiceBusTopic("session-topic")
    .AddServiceBusSubscription("session-subscription")
    .WithProperties(subscription => subscription.RequiresSession = true);
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

// Add MessageHandler console app to process messages from queue-one
builder.AddProject<Projects.MessageHandler>("message-handler")
    .WithReference(serviceBus)
    .WaitFor(serviceBus);

builder.AddLabcoat("tests");

builder.Build().Run();

using Aspire.Hosting.Azure;
using Aspire.Hosting;
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

// Add ASB Emulator UI using the extension method
//var apiService = builder.AddAsbEmulatorUi("asb-ui", serviceBus, options =>
//{
//    options.ConfigureSettings = settings =>
//    {
//        // Add some example canned messages
//        settings.CannedMessages["queue-one"] = new Dictionary<string, CannedMessage>
//        {
//            ["test-order"] = new CannedMessage
//            {
//                ContentType = "application/json",
//                Body = """{ "orderId": "~newGuid~", "timestamp": "~now~", "amount": 99.99 }""",
//                ApplicationProperties = new Dictionary<string, object>
//                {
//                    ["MessageType"] = "MT_EVENT",
//                    ["EventType"] = "OrderCreated"
//                }
//            }
//        };

//        settings.CommonApplicationProperties.Add(new KeyValuePair<string, string>("Source", "Integration-Test"));
//    };
//})
//.WithIntegrationTestApi(enabled: true);  // Enable integration test API

builder.AddAsbEmulatorUi("asb-ui", serviceBus);

builder.Build().Run();

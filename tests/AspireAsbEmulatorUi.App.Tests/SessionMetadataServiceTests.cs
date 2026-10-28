using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using AspireAsbEmulatorUi.App.Models;
using AspireAsbEmulatorUi.App.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AspireAsbEmulatorUi.App.Tests;

public class SessionMetadataServiceTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Enrich_Queue_PreservesKnownSessionRequirement(bool requiresSession)
    {
        var client = new MetadataClient { RequiresSession = requiresSession };
        var service = Create(client);
        var entity = new ServiceBusEntityInfo { Name = "Queue.MixedCase", EntityType = "Queue" };

        await service.EnrichEntitiesAsync([entity]);
        await service.EnrichEntitiesAsync([entity]);

        await Assert.That(entity.RequiresSession).IsEqualTo(requiresSession);
        await Assert.That(client.QueueName).IsEqualTo("Queue.MixedCase");
        await Assert.That(client.Calls).IsEqualTo(1);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Enrich_Subscription_UsesParentAndSubscriptionIdentity(bool requiresSession)
    {
        var client = new MetadataClient { RequiresSession = requiresSession };
        var entity = new ServiceBusEntityInfo { Name = "Topic.MixedCase|Sub.MixedCase", ParentTopic = "Topic.MixedCase", EntityType = "Subscription" };

        await Create(client).EnrichEntitiesAsync([entity]);

        await Assert.That(entity.RequiresSession).IsEqualTo(requiresSession);
        await Assert.That(client.TopicName).IsEqualTo("Topic.MixedCase");
        await Assert.That(client.SubscriptionName).IsEqualTo("Sub.MixedCase");
    }

    [Test]
    public async Task Enrich_TopicAndMalformedSubscription_RemainUnknown()
    {
        var client = new MetadataClient();
        var topic = new ServiceBusEntityInfo { Name = "topic", EntityType = "Topic" };
        var subscription = new ServiceBusEntityInfo { Name = "another|sub", ParentTopic = "topic", EntityType = "Subscription" };

        await Create(client).EnrichEntitiesAsync([topic, subscription]);

        await Assert.That(topic.RequiresSession).IsNull();
        await Assert.That(subscription.RequiresSession).IsNull();
        await Assert.That(client.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Enrich_UnconfiguredOrMalformedConfiguration_RemainsUnknown()
    {
        foreach (var values in new[]
                 {
                     new Dictionary<string, string?>(),
                     new Dictionary<string, string?> { ["asb-management-endpoint"] = "not-a-url" },
                     new Dictionary<string, string?> { ["asb-management-connectionstring"] = "invalid" }
                 })
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            var service = new SessionMetadataService(config, NullLogger<SessionMetadataService>.Instance);
            var entity = new ServiceBusEntityInfo { Name = "queue", EntityType = "Queue" };

            await service.EnrichEntitiesAsync([entity]);

            await Assert.That(entity.RequiresSession).IsNull();
        }
    }

    [Test]
    [Arguments(401)]
    [Arguments(404)]
    [Arguments(500)]
    [Arguments(501)]
    public async Task Enrich_AdministrationFailure_DoesNotPreventListing(int status)
    {
        var entity = new ServiceBusEntityInfo { Name = "queue", EntityType = "Queue" };
        var service = Create(new MetadataClient { Error = new RequestFailedException(status, "unavailable") });

        await service.EnrichEntitiesAsync([entity]);

        await Assert.That(entity.RequiresSession).IsNull();
        await Assert.That(entity.Name).IsEqualTo("queue");
    }

    [Test]
    public async Task Enrich_HungClient_IsBoundedAcrossManyEntities()
    {
        var entities = Enumerable.Range(0, 30)
            .Select(i => new ServiceBusEntityInfo { Name = $"queue-{i}", EntityType = "Queue" }).ToList();
        var service = Create(new MetadataClient { Hang = true }, TimeSpan.FromMilliseconds(50));

        await service.EnrichEntitiesAsync(entities).WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.That(entities.All(e => e.RequiresSession is null)).IsTrue();
    }

    [Test]
    public async Task Enrich_ClientIgnoringCancellation_IsStillBounded()
    {
        var service = Create(new MetadataClient { IgnoreCancellation = true }, TimeSpan.FromMilliseconds(50));
        var entity = new ServiceBusEntityInfo { Name = "queue", EntityType = "Queue" };

        await service.EnrichEntitiesAsync([entity]).WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.That(entity.RequiresSession).IsNull();
    }

    [Test]
    public async Task Enrich_SubscriptionFailure_RemainsUnknown()
    {
        var service = Create(new MetadataClient { Error = new NotSupportedException() });
        var entity = new ServiceBusEntityInfo { Name = "topic|sub", ParentTopic = "topic", EntityType = "Subscription" };

        await service.EnrichEntitiesAsync([entity]);

        await Assert.That(entity.RequiresSession).IsNull();
    }

    [Test]
    public async Task Enrich_ConcurrentSelections_DoNotShareEntityResults()
    {
        var client = new MetadataClient { RequiresSession = true };
        var service = Create(client);
        var queue = new ServiceBusEntityInfo { Name = "queue", EntityType = "Queue" };
        var topic = new ServiceBusEntityInfo { Name = "topic", EntityType = "Topic" };

        await Task.WhenAll(service.EnrichEntitiesAsync([queue]), service.EnrichEntitiesAsync([topic]));

        await Assert.That(queue.RequiresSession).IsTrue();
        await Assert.That(topic.RequiresSession).IsNull();
    }

    private static SessionMetadataService Create(MetadataClient client, TimeSpan? timeout = null) =>
        new(client, NullLogger<SessionMetadataService>.Instance, timeout ?? TimeSpan.FromSeconds(2));

    private sealed class MetadataClient : ServiceBusAdministrationClient
    {
        public bool RequiresSession { get; init; }
        public Exception? Error { get; init; }
        public bool Hang { get; init; }
        public bool IgnoreCancellation { get; init; }
        public int Calls { get; private set; }
        public string? QueueName { get; private set; }
        public string? TopicName { get; private set; }
        public string? SubscriptionName { get; private set; }

        public override async Task<Response<QueueProperties>> GetQueueAsync(string name, CancellationToken cancellationToken = default)
        {
            Calls++;
            QueueName = name;
            if (Error is not null) throw Error;
            if (Hang) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (IgnoreCancellation) await new TaskCompletionSource().Task;
            return Response.FromValue(ServiceBusModelFactory.QueueProperties(name: name, requiresSession: RequiresSession,
                lockDuration: TimeSpan.FromMinutes(1), maxSizeInMegabytes: 1024,
                defaultMessageTimeToLive: TimeSpan.FromDays(14), autoDeleteOnIdle: TimeSpan.MaxValue,
                duplicateDetectionHistoryTimeWindow: TimeSpan.FromMinutes(10), maxDeliveryCount: 10,
                userMetadata: string.Empty, status: EntityStatus.Active, maxMessageSizeInKilobytes: 256), NSubstitute.Substitute.For<Response>());
        }

        public override Task<Response<SubscriptionProperties>> GetSubscriptionAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default)
        {
            Calls++;
            TopicName = topicName;
            SubscriptionName = subscriptionName;
            if (Error is not null) throw Error;
            return Task.FromResult(Response.FromValue(ServiceBusModelFactory.SubscriptionProperties(topicName: topicName, subscriptionName: subscriptionName,
                requiresSession: RequiresSession, lockDuration: TimeSpan.FromMinutes(1),
                defaultMessageTimeToLive: TimeSpan.FromDays(14), autoDeleteOnIdle: TimeSpan.MaxValue,
                maxDeliveryCount: 10, userMetadata: string.Empty, status: EntityStatus.Active), NSubstitute.Substitute.For<Response>()));
        }
    }
}

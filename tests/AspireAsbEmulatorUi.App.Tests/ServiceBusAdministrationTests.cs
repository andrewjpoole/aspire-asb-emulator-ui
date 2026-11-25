using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using System.Xml.Linq;
using AspireAsbEmulatorUi.App.Models;
using AspireAsbEmulatorUi.App.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AspireAsbEmulatorUi.App.Tests;

public class ServiceBusAdministrationTests
{
    [Test]
    public async Task Discovery_MapsAllPagesPreservesSessionsAndUsesRuntimeCounts()
    {
        var client = new AdministrationClientFake
        {
            QueuePages = [[Queue("queue-a", true)], [Queue("queue-b", false)]],
            TopicPages = [[Topic("Topic.Mixed")], [Topic("another-topic")]],
            SubscriptionPages = [[Subscription("Topic.Mixed", "Sub.Mixed", true)], [Subscription("Topic.Mixed", "ordinary", false)]]
        };
        var entities = await Create(client).GetEntitiesAsync();
        var queue = entities.Single(e => e.Name == "queue-a");
        var subscription = entities.Single(e => e.Name == "Topic.Mixed|Sub.Mixed");

        await Assert.That(entities.Count).IsEqualTo(6);
        await Assert.That(queue.RequiresSession).IsTrue();
        await Assert.That(entities.Single(e => e.Name == "queue-b").RequiresSession).IsFalse();
        await Assert.That(subscription.RequiresSession).IsTrue();
        await Assert.That(subscription.ParentTopic).IsEqualTo("Topic.Mixed");
        await Assert.That(subscription.EntityType).IsEqualTo("Subscription");
        await Assert.That(queue.ActiveMessageCount).IsEqualTo(150L);
        await Assert.That(queue.DeadletterMessageCount).IsEqualTo(7L);
        await Assert.That(subscription.ActiveMessageCount).IsEqualTo(220L);
        await Assert.That(subscription.DeadletterMessageCount).IsEqualTo(9L);
        await Assert.That(entities.Where(e => e.EntityType != "Topic").All(e => e.MessageCountsKnown)).IsTrue();

        var busClient = new FakeClient();
        await using var bus = ServiceBusFixture.Create(busClient, new FakeEntityRepository());
        await bus.EnrichEntitiesWithMessageCountsAsync(entities);
        await Assert.That(busClient.OrdinaryCalls).IsEmpty();

        var metadata = new SessionMetadataService(new ConfigurationBuilder().Build(), NullLogger<SessionMetadataService>.Instance);
        await metadata.EnrichEntitiesAsync(entities);
        await Assert.That(subscription.RequiresSession).IsTrue();

        await using var existenceService = new ServiceBusService("", NullLogger<ServiceBusService>.Instance, Create(client));
        await Assert.That(await existenceService.EntityExistsViaRepoAsync("topic.mixed/subscriptions/sub.mixed/$DeadLetterQueue")).IsTrue();

        client.QueuePages.Reverse();
        var refreshed = await Create(client).GetEntitiesAsync();
        await Assert.That(refreshed.Single(e => e.Name == queue.Name).Id).IsEqualTo(queue.Id);
        await Assert.That(entities.Select(e => e.Id).Distinct().Count()).IsEqualTo(entities.Count);
    }

    [Test]
    [Arguments(404)]
    [Arguments(405)]
    [Arguments(501)]
    public async Task UnsupportedRuntimeCounts_UsePeekCompatibilityCounts(int status)
    {
        var client = new AdministrationClientFake { RuntimeError = new RequestFailedException(status, "unsupported") };
        var entities = await Create(client).GetEntitiesAsync();
        await Assert.That(entities.All(e => !e.MessageCountsKnown)).IsTrue();
        var busClient = new FakeClient();
        busClient.Receiver.Peek = (_, _, _) => Task.FromResult(ServiceBusFixture.Messages(2));
        await using var bus = ServiceBusFixture.Create(busClient, new FakeEntityRepository());
        await bus.EnrichEntitiesWithMessageCountsAsync(entities);

        await Assert.That(entities).IsNotEmpty();
        await Assert.That(entities.All(e => e.MessageCountsKnown && e.ActiveMessageCount == 2)).IsTrue();
        await Assert.That(busClient.OrdinaryCalls.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments(null)]
    [Arguments("iRe")]
    public async Task MissingOrObfuscatedRuntimeFields_AreNotTreatedAsSuccessfulSdkZeroCounts(string? countField)
    {
        var client = new AdministrationClientFake { ActiveCountElement = countField };
        var entities = await Create(client).GetEntitiesAsync();

        await Assert.That(entities.Single().MessageCountsKnown).IsFalse();
    }

    [Test]
    [Arguments(401)]
    [Arguments(500)]
    public async Task RuntimeFailure_IsNotMisreportedAsZeroCounts(int status)
    {
        var client = new AdministrationClientFake { RuntimeError = new RequestFailedException(status, "failure") };
        await Assert.That(async () => await Create(client).GetEntitiesAsync()).Throws<RequestFailedException>();
    }

    [Test]
    public async Task Discovery_FailureAfterQueues_DoesNotReturnPartialNamespace()
    {
        var client = new AdministrationClientFake { TopicError = new RequestFailedException(500, "failure") };
        await Assert.That(async () => await Create(client).GetEntitiesAsync()).Throws<RequestFailedException>();
    }

    [Test]
    public async Task Discovery_HungPaginationIsBoundedAndCallerCancellationPropagates()
    {
        var service = Create(new AdministrationClientFake { Hang = true }, TimeSpan.FromMilliseconds(50));
        await Assert.That(async () => await service.GetEntitiesAsync().WaitAsync(TimeSpan.FromSeconds(2))).Throws<TimeoutException>();

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => await service.GetEntitiesAsync(cancellation.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    [Arguments("Auto", 404, true)]
    [Arguments("Auto", 503, true)]
    [Arguments("Auto", 401, false)]
    [Arguments("Auto", 403, false)]
    [Arguments("Auto", 500, false)]
    [Arguments("Administration", 404, false)]
    [Arguments("Administration", 503, false)]
    public async Task DiscoveryMode_RestrictsLazySqlFallback(string mode, int status, bool shouldFallBack)
    {
        var client = new AdministrationClientFake { QueueError = new RequestFailedException(status, "failure") };
        var sqlCalls = 0;
        var service = new ServiceBusEntityRepository(Options(mode), Create(client), () =>
        {
            sqlCalls++;
            return new FakeEntityRepository();
        }, NullLogger<ServiceBusEntityRepository>.Instance);

        if (shouldFallBack)
            await Assert.That(await service.GetEntitiesAsync()).IsNotEmpty();
        else
            await Assert.That(async () => await service.GetEntitiesAsync()).Throws<RequestFailedException>();

        await Assert.That(sqlCalls).IsEqualTo(shouldFallBack ? 1 : 0);
    }

    [Test]
    public async Task Auto_EmptyAdministrationNamespaceDoesNotFallBackToSql()
    {
        var client = new AdministrationClientFake { QueuePages = [], TopicPages = [] };
        var service = new ServiceBusEntityRepository(Options("Auto"), Create(client),
            () => throw new InvalidOperationException("SQL must not be instantiated"), NullLogger<ServiceBusEntityRepository>.Instance);

        await Assert.That(await service.GetEntitiesAsync()).IsEmpty();
    }

    [Test]
    public async Task Auto_WithoutSqlConfigurationDoesNotAttemptSqlOnAdministrationFailure()
    {
        var client = new AdministrationClientFake { QueueError = new RequestFailedException(404, "unsupported") };
        var service = new ServiceBusEntityRepository(Options("Auto", configureSql: false), Create(client),
            () => throw new InvalidOperationException("SQL must not be instantiated"), NullLogger<ServiceBusEntityRepository>.Instance);

        await Assert.That(async () => await service.GetEntitiesAsync()).Throws<RequestFailedException>();
    }

    [Test]
    public async Task SqlMode_DoesNotCallAdministration()
    {
        var client = new AdministrationClientFake { QueueError = new InvalidOperationException("Administration must not be called") };
        var service = new ServiceBusEntityRepository(Options("Sql"), Create(client), () => new FakeEntityRepository(),
            NullLogger<ServiceBusEntityRepository>.Instance);
        await Assert.That(await service.GetEntitiesAsync()).IsNotEmpty();
    }

    [Test]
    public async Task CancelledDiscovery_DoesNotInstantiateSql()
    {
        var service = new ServiceBusEntityRepository(Options("Auto"), Create(new AdministrationClientFake()),
            () => throw new InvalidOperationException("SQL must not be instantiated"), NullLogger<ServiceBusEntityRepository>.Instance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => await service.GetEntitiesAsync(cancellation.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task LegacyPeeking_ReportsFailedCountsAsUnknownAndSkipsTopics()
    {
        var client = new FakeClient();
        client.Receiver.Peek = (_, _, _) => throw new ServiceBusException("unavailable", ServiceBusFailureReason.ServiceCommunicationProblem);
        await using var service = ServiceBusFixture.Create(client, new FakeEntityRepository());
        var queue = new ServiceBusEntityInfo { Name = "queue", EntityType = "Queue", ActiveMessageCount = 99 };
        var topic = new ServiceBusEntityInfo { Name = "topic", EntityType = "Topic" };

        await service.EnrichEntitiesWithMessageCountsAsync([queue, topic]);

        await Assert.That(queue.MessageCountsKnown).IsFalse();
        await Assert.That(client.OrdinaryCalls.Count).IsEqualTo(2);
        await Assert.That(client.Receiver.Disposed).IsTrue();
    }

    [Test]
    public async Task LegacyPeeking_RefreshesCountsOnRepeatedEnrichment()
    {
        var client = new FakeClient();
        await using var service = ServiceBusFixture.Create(client, new FakeEntityRepository());
        var entity = new ServiceBusEntityInfo { Name = "queue", EntityType = "Queue" };
        client.Receiver.Peek = (_, _, _) => Task.FromResult(ServiceBusFixture.Messages(1));
        await service.EnrichEntitiesWithMessageCountsAsync([entity]);
        client.Receiver.Peek = (_, _, _) => Task.FromResult(ServiceBusFixture.Messages(2));
        await service.EnrichEntitiesWithMessageCountsAsync([entity]);

        await Assert.That(entity.ActiveMessageCount).IsEqualTo(2L);
        await Assert.That(entity.MessageCountsKnown).IsTrue();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task LegacyCountApi_PreservesAvailableSubqueueCountsWhenTheOtherFails(bool mainFails)
    {
        var client = new FakeClient();
        client.Receiver.Peek = (_, _, _) =>
            client.OrdinaryCalls.Last().Path.EndsWith("/$DeadLetterQueue") == mainFails
                ? Task.FromResult(ServiceBusFixture.Messages(3))
                : throw new ServiceBusException("unavailable", ServiceBusFailureReason.ServiceCommunicationProblem);
        await using var service = ServiceBusFixture.Create(client, new FakeEntityRepository());

        var counts = await service.GetMessageCountsByPeekingAsync("queue");

        await Assert.That(counts.ActiveCount).IsEqualTo(mainFails ? 0L : 3L);
        await Assert.That(counts.DeadLetterCount).IsEqualTo(mainFails ? 3L : 0L);
    }

    internal static ServiceBusAdministrationService Create(AdministrationClientFake client, TimeSpan? timeout = null) =>
        new(client, NullLogger<ServiceBusAdministrationService>.Instance, timeout ?? TimeSpan.FromSeconds(2));

    private static ServiceBusAdministrationOptions Options(string mode, bool configureSql = true) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASB_ENTITY_DISCOVERY"] = mode,
            ["ASB_MANAGEMENT_ENDPOINT"] = "http://localhost:5300",
            ["ASB_SQL_CONNECTIONSTRING"] = configureSql ? "configured but intentionally never connected" : null
        }).Build());

    internal static QueueProperties Queue(string name, bool requiresSession = false) =>
        ServiceBusModelFactory.QueueProperties(name: name, requiresSession: requiresSession,
            lockDuration: TimeSpan.FromMinutes(1), maxSizeInMegabytes: 1024,
            defaultMessageTimeToLive: TimeSpan.FromHours(1), autoDeleteOnIdle: TimeSpan.MaxValue,
            duplicateDetectionHistoryTimeWindow: TimeSpan.FromMinutes(1), maxDeliveryCount: 10,
            userMetadata: "", status: EntityStatus.Active, maxMessageSizeInKilobytes: 256);

    internal static TopicProperties Topic(string name) =>
        ServiceBusModelFactory.TopicProperties(name: name, maxSizeInMegabytes: 1024,
            defaultMessageTimeToLive: TimeSpan.FromHours(1), autoDeleteOnIdle: TimeSpan.MaxValue,
            duplicateDetectionHistoryTimeWindow: TimeSpan.FromMinutes(1), maxMessageSizeInKilobytes: 256);

    internal static SubscriptionProperties Subscription(string topic, string name, bool requiresSession = false) =>
        ServiceBusModelFactory.SubscriptionProperties(topicName: topic, subscriptionName: name, requiresSession: requiresSession,
            lockDuration: TimeSpan.FromMinutes(1), defaultMessageTimeToLive: TimeSpan.FromHours(1),
            autoDeleteOnIdle: TimeSpan.MaxValue, maxDeliveryCount: 10, userMetadata: "", status: EntityStatus.Active);

    internal sealed class AdministrationClientFake : ServiceBusAdministrationClient
    {
        public List<QueueProperties[]> QueuePages { get; set; } = [[Queue("queue")]];
        public List<TopicProperties[]> TopicPages { get; set; } = [];
        public List<SubscriptionProperties[]> SubscriptionPages { get; set; } = [];
        public Exception? QueueError { get; init; }
        public Exception? TopicError { get; init; }
        public Exception? RuntimeError { get; init; }
        public bool Hang { get; init; }
        public string? ActiveCountElement { get; init; } = "ActiveMessageCount";

        public override AsyncPageable<QueueProperties> GetQueuesAsync(CancellationToken cancellationToken = default)
        {
            if (QueueError is not null) throw QueueError;
            return Hang ? new HungPages<QueueProperties>() : Pages(QueuePages);
        }

        public override AsyncPageable<TopicProperties> GetTopicsAsync(CancellationToken cancellationToken = default)
        {
            if (TopicError is not null) throw TopicError;
            return Pages(TopicPages);
        }

        public override AsyncPageable<SubscriptionProperties> GetSubscriptionsAsync(string topicName, CancellationToken cancellationToken = default) =>
            Pages(SubscriptionPages.Select(page => page.Where(s => s.TopicName == topicName).ToArray()));

        public override AsyncPageable<QueueRuntimeProperties> GetQueuesRuntimePropertiesAsync(CancellationToken cancellationToken = default)
        {
            if (RuntimeError is not null) throw RuntimeError;
            return Pages(QueuePages.Select(page => page.Select(q => ServiceBusModelFactory.QueueRuntimeProperties(
                name: q.Name, activeMessageCount: 150, deadLetterMessageCount: 7)).ToArray()), ActiveCountElement);
        }

        public override AsyncPageable<SubscriptionRuntimeProperties> GetSubscriptionsRuntimePropertiesAsync(string topicName, CancellationToken cancellationToken = default)
        {
            if (RuntimeError is not null) throw RuntimeError;
            return Pages(SubscriptionPages.Select(page => page.Where(s => s.TopicName == topicName).Select(s =>
                ServiceBusModelFactory.SubscriptionRuntimeProperties(topicName: topicName, subscriptionName: s.SubscriptionName,
                    activeMessageCount: 220, deadLetterMessageCount: 9)).ToArray()), ActiveCountElement);
        }

        private static AsyncPageable<T> Pages<T>(IEnumerable<T[]> pages, string? activeCountElement = "ActiveMessageCount") where T : notnull =>
            AsyncPageable<T>.FromPages(pages.Select((values, i) =>
            {
                XNamespace atom = "http://www.w3.org/2005/Atom";
                var response = Substitute.For<Response>();
                response.Content.Returns(BinaryData.FromString(new XElement(atom + "feed", values.Select(value =>
                    new XElement(atom + "entry",
                        new XElement(atom + "title", value switch
                        {
                            QueueRuntimeProperties queue => queue.Name,
                            SubscriptionRuntimeProperties subscription => subscription.SubscriptionName,
                            _ => ""
                        }),
                        activeCountElement is null ? null : new XElement("CountDetails",
                            new XElement(activeCountElement, 0), new XElement("DeadLetterMessageCount", 0))))).ToString()));
                return Page<T>.FromValues(values, i.ToString(), response);
            }));
    }

    private sealed class HungPages<T> : AsyncPageable<T> where T : notnull
    {
        public override async IAsyncEnumerable<Page<T>> AsPages(string? continuationToken = null, int? pageSizeHint = null)
        {
            await new TaskCompletionSource().Task;
            yield break;
        }
    }
}

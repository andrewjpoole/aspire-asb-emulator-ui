using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Azure;
using Azure.Messaging.ServiceBus.Administration;
using AspireAsbEmulatorUi.App.Models;

namespace AspireAsbEmulatorUi.App.Services;

public sealed class ServiceBusAdministrationService : IServiceBusEntityRepository
{
    private readonly ServiceBusAdministrationClient? _client;
    private readonly ILogger<ServiceBusAdministrationService> _logger;
    private readonly TimeSpan _timeout;

    public ServiceBusAdministrationService(ServiceBusAdministrationOptions options, ILogger<ServiceBusAdministrationService> logger)
        : this(options.CreateClient(), logger, ServiceBusAdministrationOptions.OperationTimeout) { }

    internal ServiceBusAdministrationService(ServiceBusAdministrationClient? client, ILogger<ServiceBusAdministrationService> logger, TimeSpan timeout)
    {
        _client = client;
        _logger = logger;
        _timeout = timeout;
    }

    public async Task<List<ServiceBusEntityInfo>> GetEntitiesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_client is null)
            throw new InvalidOperationException("Service Bus administration endpoint is not configured.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            return await DiscoverAsync(timeout.Token).WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Service Bus administration discovery timed out. Check the management endpoint and emulator availability.");
        }
    }

    private async Task<List<ServiceBusEntityInfo>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var entities = new List<ServiceBusEntityInfo>();
        await foreach (var queue in _client!.GetQueuesAsync(cancellationToken).WithCancellation(cancellationToken))
            entities.Add(CreateEntity("Queue", queue.Name, requiresSession: queue.RequiresSession));

        var topics = new List<string>();
        await foreach (var topic in _client.GetTopicsAsync(cancellationToken).WithCancellation(cancellationToken))
        {
            topics.Add(topic.Name);
            entities.Add(CreateEntity("Topic", topic.Name));
            await foreach (var subscription in _client.GetSubscriptionsAsync(topic.Name, cancellationToken).WithCancellation(cancellationToken))
                entities.Add(CreateEntity("Subscription", $"{topic.Name}|{subscription.SubscriptionName}", topic.Name, subscription.RequiresSession));
        }

        try
        {
            var queues = entities.Where(e => e.EntityType == "Queue").ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
            if (queues.Count > 0)
            {
                await foreach (var page in _client.GetQueuesRuntimePropertiesAsync(cancellationToken).AsPages().WithCancellation(cancellationToken))
                {
                    var countNames = GetEntitiesWithRuntimeCounts(page.GetRawResponse());
                    foreach (var runtime in page.Values)
                        if (countNames.Contains(runtime.Name) && queues.TryGetValue(runtime.Name, out var entity))
                            SetCounts(entity, runtime.ActiveMessageCount, runtime.DeadLetterMessageCount);
                }
            }
            foreach (var topic in topics)
            {
                var subscriptions = entities.Where(e => e.ParentTopic == topic).ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
                if (subscriptions.Count == 0) continue;
                await foreach (var page in _client.GetSubscriptionsRuntimePropertiesAsync(topic, cancellationToken).AsPages().WithCancellation(cancellationToken))
                {
                    var countNames = GetEntitiesWithRuntimeCounts(page.GetRawResponse());
                    foreach (var runtime in page.Values)
                        if (countNames.Contains(runtime.SubscriptionName) && subscriptions.TryGetValue($"{topic}|{runtime.SubscriptionName}", out var entity))
                            SetCounts(entity, runtime.ActiveMessageCount, runtime.DeadLetterMessageCount);
                }
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or RequestFailedException { Status: 400 or 404 or 405 or 501 })
        {
            _logger.LogInformation("Administration runtime counts are unsupported ({ExceptionType}); using bounded peek counts where available.", ex.GetType().Name);
        }

        return entities.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static HashSet<string> GetEntitiesWithRuntimeCounts(Response response)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Microsoft emulator 2.0.1 omits queue counts and emits obfuscated subscription count fields.
        // The SDK silently maps both to zero, so trust counts only when the actual fields are present.
        if (response.Content is not { } content) return names;
        try
        {
            XNamespace atom = "http://www.w3.org/2005/Atom";
            var document = XDocument.Parse(content.ToString());
            foreach (var entry in document.Descendants(atom + "entry"))
            {
                if (entry.Descendants().Any(e => e.Name.LocalName == "ActiveMessageCount") &&
                    entry.Descendants().Any(e => e.Name.LocalName == "DeadLetterMessageCount") &&
                    entry.Element(atom + "title") is { } title)
                    names.Add(title.Value);
            }
        }
        catch (XmlException)
        {
            // A response without verifiable count fields is not a successful zero count.
        }
        return names;
    }

    private static ServiceBusEntityInfo CreateEntity(string type, string name, string? parent = null, bool? requiresSession = null)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{type}:{name.ToUpperInvariant()}"));
        return new ServiceBusEntityInfo
        {
            // SQL IDs are private implementation details; administration identities must survive refreshes and paging order changes.
            Id = BinaryPrimitives.ReadInt64BigEndian(hash) & long.MaxValue,
            Name = name,
            EntityType = type,
            ParentTopic = parent,
            RequiresSession = requiresSession,
            DiscoveredViaAdministration = true
        };
    }

    private static void SetCounts(ServiceBusEntityInfo entity, long active, long deadLetter)
    {
        entity.ActiveMessageCount = active;
        entity.DeadletterMessageCount = deadLetter;
        entity.MessageCountsKnown = true;
    }
}

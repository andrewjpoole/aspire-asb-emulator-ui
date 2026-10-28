using System.Collections.Concurrent;
using Azure.Messaging.ServiceBus.Administration;
using AspireAsbEmulatorUi.App.Models;

namespace AspireAsbEmulatorUi.App.Services;

/// <summary>Best-effort, bounded session metadata lookup through the emulator management endpoint.</summary>
public sealed class SessionMetadataService
{
    private readonly ServiceBusAdministrationClient? _client;
    private readonly ILogger<SessionMetadataService> _logger;
    private readonly TimeSpan _lookupTimeout;
    private readonly ConcurrentDictionary<(string Type, string Name, string? Parent), (bool? Value, DateTimeOffset Expires)> _cache = new();

    /// <summary>Creates an optional client from explicit management configuration; AMQP ports are never guessed.</summary>
    public SessionMetadataService(IConfiguration configuration, ILogger<SessionMetadataService> logger)
    {
        _logger = logger;
        _lookupTimeout = TimeSpan.FromSeconds(2);
        var connectionString = configuration["asb-management-connectionstring"] ?? configuration["ASB_MANAGEMENT_CONNECTIONSTRING"];
        var endpoint = configuration["asb-management-endpoint"] ?? configuration["ASB_MANAGEMENT_ENDPOINT"];
        if (string.IsNullOrWhiteSpace(connectionString) && string.IsNullOrWhiteSpace(endpoint)) return;

        try
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != "http" && uri.Scheme != "https") ||
                    !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" ||
                    !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                {
                    logger.LogDebug("Session metadata management endpoint is invalid; metadata remains unknown.");
                    return;
                }

                // Aspire's emulator connection string uses these fixed development credentials.
                // The administration SDK selects plain HTTP when UseDevelopmentEmulator=true.
                connectionString = $"Endpoint=sb://{uri.Authority};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";
            }

            var options = new ServiceBusAdministrationClientOptions();
            options.Retry.MaxRetries = 0;
            options.Retry.NetworkTimeout = _lookupTimeout;
            _client = new ServiceBusAdministrationClient(connectionString, options);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            logger.LogDebug("Session metadata management configuration is invalid; metadata remains unknown.");
        }
    }

    // Test-only seam: supply a fake SDK client to exercise bounded metadata lookups without a running emulator.
    // Keep internal so DI and application callers use the configuration-based constructor.
    internal SessionMetadataService(ServiceBusAdministrationClient client, ILogger<SessionMetadataService> logger, TimeSpan lookupTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lookupTimeout, TimeSpan.Zero);
        _client = client;
        _logger = logger;
        _lookupTimeout = lookupTimeout;
    }

    /// <summary>Enriches only queue/subscription session flags; unavailable metadata stays unknown.</summary>
    public async Task EnrichEntitiesAsync(IReadOnlyList<ServiceBusEntityInfo> entities, CancellationToken cancellationToken = default)
    {
        foreach (var entity in entities) entity.RequiresSession = null;
        if (_client is null) return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_lookupTimeout);
        try
        {
            await Parallel.ForEachAsync(entities, new ParallelOptions
            {
                MaxDegreeOfParallelism = 8,
                CancellationToken = timeout.Token
            }, async (entity, token) =>
            {
                var key = (entity.EntityType, entity.Name, entity.ParentTopic);
                if (_cache.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
                {
                    entity.RequiresSession = cached.Value;
                    return;
                }

                var value = await LookupAsync(entity, token);
                _cache[key] = (value, DateTimeOffset.UtcNow.AddSeconds(30));
                entity.RequiresSession = value;
            });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Session metadata batch timed out; unresolved entities remain unknown.");
        }
    }

    private async Task<bool?> LookupAsync(ServiceBusEntityInfo entity, CancellationToken cancellationToken)
    {
        try
        {
            if (string.Equals(entity.EntityType, "Queue", StringComparison.OrdinalIgnoreCase))
                return (await _client!.GetQueueAsync(entity.Name, cancellationToken).WaitAsync(cancellationToken)).Value.RequiresSession;

            if (string.Equals(entity.EntityType, "Subscription", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(entity.ParentTopic) &&
                entity.Name.StartsWith(entity.ParentTopic + "|", StringComparison.Ordinal))
            {
                var name = entity.Name[(entity.ParentTopic.Length + 1)..];
                if (string.IsNullOrWhiteSpace(name) || name.Contains('|')) return null;
                return (await _client!.GetSubscriptionAsync(entity.ParentTopic, name, cancellationToken).WaitAsync(cancellationToken)).Value.RequiresSession;
            }
        }
        catch (Exception ex)
        {
            // Administration support is optional; never fail SQL entity discovery or assume false.
            _logger.LogDebug("Session metadata lookup unavailable ({ExceptionType}); requirement remains unknown.", ex.GetType().Name);
        }
        return null;
    }
}

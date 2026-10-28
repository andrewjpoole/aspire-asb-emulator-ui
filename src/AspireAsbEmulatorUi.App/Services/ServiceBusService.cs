using Azure.Messaging.ServiceBus;
using System.Text;
using AspireAsbEmulatorUi.App.Models;

namespace AspireAsbEmulatorUi.App.Services;

public class ServiceBusService : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<ServiceBusService> _logger;
    private readonly AsbEmulatorSqlEntityRepository _repo;
    private ServiceBusClient? _client;
    private readonly object _lockedClientSync = new();
    private ServiceBusClient? _lockedClient;
    private readonly Func<string, ServiceBusClient> _lockedClientFactory = cs => new ServiceBusClient(cs,
        new ServiceBusClientOptions { RetryOptions = new ServiceBusRetryOptions { MaxRetries = 0 } });
    private readonly Func<string, ServiceBusClient> _clientFactory = cs => new ServiceBusClient(cs);
    private readonly TimeProvider _timeProvider = TimeProvider.System;
    internal const int DrainBatchSize = 100;
    internal const int DrainMessageLimit = 10_000;
    internal static readonly TimeSpan DrainTimeLimit = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan ReceiveIdleWait = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan SessionAcceptTimeout = TimeSpan.FromSeconds(5);
    internal const int PeekScanLimit = 10_000;
    internal static readonly TimeSpan ScanTimeLimit = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(2);

    public ServiceBusService(string connectionString, ILogger<ServiceBusService> logger, AsbEmulatorSqlEntityRepository repo)
    {
        _connectionString = connectionString;
        _logger = logger;
        _repo = repo;
        if (!string.IsNullOrWhiteSpace(_connectionString))
        {
            var preview = _connectionString.Length > 80 ? _connectionString[..80] + "..." : _connectionString;
            _logger.LogInformation("ServiceBusService created with connection string: {Preview}", preview);
        }
        else
        {
            _logger.LogWarning("ServiceBusService created with empty connection string.");
        }
    }

    // Allows tests to supply SDK fakes and a controllable clock without a live broker,
    // while preserving the public constructor and lazy client ownership in production.
    internal ServiceBusService(string connectionString, ILogger<ServiceBusService> logger,
        AsbEmulatorSqlEntityRepository repo, Func<string, ServiceBusClient> clientFactory, TimeProvider? timeProvider = null,
        Func<string, ServiceBusClient>? lockedClientFactory = null)
        : this(connectionString, logger, repo)
    {
        _clientFactory = clientFactory;
        _lockedClientFactory = lockedClientFactory ?? clientFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private ServiceBusClient LockedClient
    {
        get
        {
            lock (_lockedClientSync)
                return _lockedClient ??= _lockedClientFactory(NormalizeConnectionString(_connectionString));
        }
    }

    /// <summary>
    /// Receives a bounded PeekLock batch owned by the caller, retaining original SDK identities for settlement.
    /// No renewal or automatic settlement retries. Empty/failure/cancelled acquisitions close their receiver.
    /// </summary>
    public async Task<LockedReceiveResult> ReceiveLockedMessagesAsync(string entity, int maxMessages = 20,
        SessionReceiveOptions? sessionOptions = null, CancellationToken cancellationToken = default)
    {
        var clean = CleanEntityName(entity);
        var selection = EffectiveSelection(clean, sessionOptions);
        ServiceBusReceiver? receiver = null;
        string? acceptedId = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(DrainTimeLimit);
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (!IsValidSelection(selection)) return new(null, MessageOperationStatus.InvalidSessionId);
            if (maxMessages <= 0 || maxMessages > DrainBatchSize) return new(null, MessageOperationStatus.Failed);
            if (!await EntityExistsViaRepoAsync(clean, deadline.Token, throwOnFailure: true))
                return new(null, MessageOperationStatus.EntityNotFound);
            var acquisition = CreateReceiveReceiverAsync(clean, selection, deadline.Token, ServiceBusReceiveMode.PeekLock, LockedClient);
            try { receiver = await acquisition.WaitAsync(deadline.Token); }
            catch
            {
                _ = CloseLateAcquisitionAsync(acquisition, clean);
                throw;
            }
            acceptedId = (receiver as ServiceBusSessionReceiver)?.SessionId;
            var receive = receiver.ReceiveMessagesAsync(maxMessages, ReceiveIdleWait, deadline.Token);
            IReadOnlyList<ServiceBusReceivedMessage> messages;
            try { messages = await receive.WaitAsync(deadline.Token); }
            catch
            {
                // Transfer cleanup to an observer; do not close against an unfinished SDK receive.
                _ = CloseAfterReceiveAsync(receive, receiver, clean);
                receiver = null;
                throw;
            }
            deadline.Token.ThrowIfCancellationRequested();
            if (messages.Count == 0) return new(null, MessageOperationStatus.Completed, acceptedId);
            var isDlq = clean.EndsWith("/$deadletterqueue", StringComparison.OrdinalIgnoreCase);
            var (destination, replayToTopic) = GetReplayDestination(clean);
            var batch = new LockedMessageBatch(receiver, messages, ConvertToDisplayedMessage,
                isDlq, _timeProvider, _logger, replayDestination: destination, replayToTopic: replayToTopic,
                senderFactory: destination is null ? null : () => LockedClient.CreateSender(destination));
            receiver = null; // Exactly one transfer; viewer must dispose even if its result is stale.
            if (deadline.IsCancellationRequested)
            {
                await batch.DisposeAsync();
                return new(null, cancellationToken.IsCancellationRequested ? MessageOperationStatus.Cancelled : MessageOperationStatus.Failed, acceptedId);
            }
            return new(batch, MessageOperationStatus.Completed, acceptedId);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return new(null, cancellationToken.IsCancellationRequested ? MessageOperationStatus.Cancelled : MessageOperationStatus.Failed, acceptedId);
        }
        catch (ServiceBusException ex) when (acceptedId is null && selection.Mode != SessionReceiveMode.Standard && ex.Reason == ServiceBusFailureReason.ServiceTimeout)
        { return new(null, MessageOperationStatus.SessionUnavailable); }
        catch (ServiceBusException ex) when (selection.Mode != SessionReceiveMode.Standard && ex.Reason == ServiceBusFailureReason.SessionCannotBeLocked)
        { return new(null, MessageOperationStatus.SessionLocked, acceptedId); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Locked batch acquisition failed");
            return new(null, MessageOperationStatus.Failed, acceptedId);
        }
        finally
        {
            if (receiver is not null) await DisposeReceiverBoundedAsync(receiver, clean);
        }
    }

    internal static (string? Destination, bool ToTopic) GetReplayDestination(string entity)
    {
        var clean = CleanEntityName(entity);
        const string suffix = "/$deadletterqueue";
        if (!clean.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return (null, false);
        var source = clean[..^suffix.Length];
        var parts = source.Split("/subscriptions/", StringSplitOptions.None);
        if (parts.Length > 2 || parts.Any(string.IsNullOrWhiteSpace) || source.Split('/').Any(string.IsNullOrWhiteSpace) ||
            (parts.Length == 2 && parts[1].Contains('/')) || source.Contains('$') || source.Contains('|'))
            return (null, false);
        return (parts[0], parts.Length == 2);
    }

    internal static ServiceBusMessage CreateReplayMessage(ServiceBusReceivedMessage original, string freshMessageId)
    {
        // Explicit allow-list: raw body and sendable properties only, never received lock/delivery metadata.
        var copy = new ServiceBusMessage(original.Body)
        {
            MessageId = freshMessageId,
            // PartitionKey is assigned before SessionId: explicitly assigning null after a session ID
            // triggers the SDK's equal-partition/session guard even for a valid unpartitioned message.
            PartitionKey = original.PartitionKey,
            SessionId = original.SessionId,
            ReplyToSessionId = original.ReplyToSessionId,
            CorrelationId = original.CorrelationId,
            ContentType = original.ContentType,
            Subject = original.Subject,
            To = original.To,
            ReplyTo = original.ReplyTo,
            TransactionPartitionKey = original.TransactionPartitionKey,
            TimeToLive = original.TimeToLive,
            ScheduledEnqueueTime = default
        };
        foreach (var property in original.ApplicationProperties)
            if (!property.Key.Equals("DeadLetterReason", StringComparison.OrdinalIgnoreCase) &&
                !property.Key.Equals("DeadLetterErrorDescription", StringComparison.OrdinalIgnoreCase))
                copy.ApplicationProperties.Add(property.Key, property.Value);
        return copy;
    }

    private async Task CloseLateAcquisitionAsync(Task<ServiceBusReceiver> acquisition, string clean)
    {
        try { await DisposeReceiverBoundedAsync(await acquisition, clean); }
        catch (Exception ex) { _logger.LogWarning(ex, "Late receiver acquisition failed"); }
    }

    private async Task CloseAfterReceiveAsync(Task<IReadOnlyList<ServiceBusReceivedMessage>> receive, ServiceBusReceiver receiver, string clean)
    {
        try { await receive; }
        catch (Exception ex) { _logger.LogWarning(ex, "Late locked receive failed"); }
        finally { await DisposeReceiverBoundedAsync(receiver, clean); }
    }

    /// <summary>Destructively receives and deletes one batch from an ordinary receiver or one explicitly accepted session.</summary>
    public async Task<ReceiveMessagesResult> ReceiveMessagesWithSessionAsync(string queueName, int maxMessages = 20,
        SessionReceiveOptions? sessionOptions = null, CancellationToken cancellationToken = default)
    {
        var clean = CleanEntityName(queueName);
        var selection = EffectiveSelection(clean, sessionOptions);
        string? acceptedId = null;
        IReadOnlyList<DisplayedMessage> messages = [];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(DrainTimeLimit);
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (!IsValidSelection(selection)) return new(messages, MessageOperationStatus.InvalidSessionId);
            if (!await EntityExistsViaRepoAsync(clean, deadline.Token, throwOnFailure: true)) return new(messages, MessageOperationStatus.EntityNotFound);
            await using var receiver = await CreateReceiveReceiverAsync(clean, selection, deadline.Token);
            acceptedId = (receiver as ServiceBusSessionReceiver)?.SessionId;
            var batch = await receiver.ReceiveMessagesAsync(maxMessages, ReceiveIdleWait, deadline.Token);
            messages = batch.Select(ConvertToDisplayedMessage).ToList();
            return new(messages, MessageOperationStatus.Completed, acceptedId);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return new(messages, cancellationToken.IsCancellationRequested ? MessageOperationStatus.Cancelled : MessageOperationStatus.Failed, acceptedId);
        }
        catch (ServiceBusException ex) when (acceptedId is null && selection.Mode != SessionReceiveMode.Standard && ex.Reason == ServiceBusFailureReason.ServiceTimeout)
        {
            return new(messages, MessageOperationStatus.SessionUnavailable);
        }
        catch (ServiceBusException ex) when (selection.Mode != SessionReceiveMode.Standard && ex.Reason == ServiceBusFailureReason.SessionCannotBeLocked)
        {
            return new(messages, MessageOperationStatus.SessionLocked, acceptedId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Receive failed for '{Entity}'", clean);
            return new(messages, MessageOperationStatus.Failed, acceptedId);
        }
    }

    private static SessionReceiveOptions EffectiveSelection(string clean, SessionReceiveOptions? selection)
        => clean.EndsWith("/$deadletterqueue", StringComparison.OrdinalIgnoreCase) ? new() : selection ?? new();

    private static bool IsValidSelection(SessionReceiveOptions selection)
        => selection.Mode == SessionReceiveMode.Standard || selection.Mode == SessionReceiveMode.NextAvailable ||
           (selection.Mode == SessionReceiveMode.Specified && !string.IsNullOrWhiteSpace(selection.SessionId));

    private async Task<ServiceBusReceiver> CreateReceiveReceiverAsync(string clean, SessionReceiveOptions selection, CancellationToken cancellationToken,
        ServiceBusReceiveMode receiveMode = ServiceBusReceiveMode.ReceiveAndDelete, ServiceBusClient? client = null)
    {
        client ??= Client;
        if (selection.Mode == SessionReceiveMode.Standard)
            return client.CreateReceiver(clean, new ServiceBusReceiverOptions { ReceiveMode = receiveMode, PrefetchCount = 0 });

        using var acceptDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        acceptDeadline.CancelAfter(SessionAcceptTimeout);
        var options = new ServiceBusSessionReceiverOptions { ReceiveMode = receiveMode, PrefetchCount = 0 };
        var parts = clean.Split("/subscriptions/", 2, StringSplitOptions.None);
        try
        {
            if (parts.Length == 2)
                return selection.Mode == SessionReceiveMode.NextAvailable
                    ? await client.AcceptNextSessionAsync(parts[0], parts[1], options, acceptDeadline.Token)
                    : await client.AcceptSessionAsync(parts[0], parts[1], selection.SessionId!, options, acceptDeadline.Token);
            return selection.Mode == SessionReceiveMode.NextAvailable
                ? await client.AcceptNextSessionAsync(clean, options, acceptDeadline.Token)
                : await client.AcceptSessionAsync(clean, selection.SessionId!, options, acceptDeadline.Token);
        }
        catch (OperationCanceledException) when (acceptDeadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ServiceBusException("Session acceptance timed out.", ServiceBusFailureReason.ServiceTimeout);
        }
    }

    /// <summary>Drains only the selected target within bounded message, duration and idle limits.</summary>
    public Task<DrainMessagesResult> DrainMessagesAsync(string queueName, SessionReceiveOptions? sessionOptions = null,
        CancellationToken cancellationToken = default)
        => DrainMessagesCoreAsync(queueName, sessionOptions, DrainMessageLimit, cancellationToken);

    /// <summary>
    /// Drains currently available messages through an ordinary ReceiveAndDelete receiver within the supplied
    /// remaining budget (capped at 10,000). DLQ draining does not depend on Main session configuration.
    /// Supply an overall cancellation deadline when combining this with retained-message completion.
    /// </summary>
    public Task<DrainMessagesResult> DrainAvailableMessagesAsync(string queueName, int maxMessages,
        CancellationToken cancellationToken = default)
        => DrainMessagesCoreAsync(queueName, null, Math.Clamp(maxMessages, 0, DrainMessageLimit), cancellationToken);

    private async Task<DrainMessagesResult> DrainMessagesCoreAsync(string queueName, SessionReceiveOptions? sessionOptions,
        int maxMessages, CancellationToken cancellationToken)
    {
        var progress = new DrainProgress();
        var clean = CleanEntityName(queueName);
        var selection = EffectiveSelection(clean, sessionOptions);
        string? acceptedId = null;
        var started = _timeProvider.GetUtcNow();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(DrainTimeLimit);
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (maxMessages == 0) return new(0, DrainStopReason.MessageLimit);
            if (!IsValidSelection(selection)) return new(progress.Count, DrainStopReason.InvalidSessionId);
            if (!await EntityExistsViaRepoAsync(clean, deadline.Token, throwOnFailure: true))
                return new(progress.Count, DrainStopReason.EntityNotFound);
            await using var receiver = await CreateReceiveReceiverAsync(clean, selection, deadline.Token);
            acceptedId = (receiver as ServiceBusSessionReceiver)?.SessionId;
            var stop = await DrainReceiverCoreAsync(receiver, progress, started, deadline.Token, maxMessages);
            return new(progress.Count, stop, acceptedId);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return new(progress.Count, cancellationToken.IsCancellationRequested ? DrainStopReason.Cancelled : DrainStopReason.TimeLimit, acceptedId);
        }
        catch (ServiceBusException ex) when (acceptedId is null && selection.Mode != SessionReceiveMode.Standard && ex.Reason == ServiceBusFailureReason.ServiceTimeout)
        {
            return new(progress.Count, DrainStopReason.SessionUnavailable);
        }
        catch (ServiceBusException ex) when (selection.Mode != SessionReceiveMode.Standard && ex.Reason == ServiceBusFailureReason.SessionCannotBeLocked)
        {
            return new(progress.Count, DrainStopReason.SessionLocked, acceptedId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bounded drain failed for '{Entity}' after {Count} returned messages", clean, progress.Count);
            return new(progress.Count, DrainStopReason.Failed, acceptedId);
        }
    }

    /// <summary>
    /// Best-effort ReceiveAndDelete drain of all currently available unlocked active sessions, bounded globally
    /// to 10,000 returned messages and 30 seconds. Locked, scheduled and deferred messages may remain.
    /// SessionCount counts accepted receivers, including a repeated ID stopped without receiving.
    /// Dead-letter subqueues use the ordinary drain. No destructive operation is retried or falls back.
    /// </summary>
    public async Task<DrainMessagesResult> DrainAllAvailableSessionsAsync(string queueName, CancellationToken cancellationToken = default)
    {
        var clean = CleanEntityName(queueName);
        if (clean.EndsWith("/$deadletterqueue", StringComparison.OrdinalIgnoreCase))
            return await DrainMessagesAsync(queueName, cancellationToken: cancellationToken);
        var progress = new DrainProgress();
        var sessionCount = 0;
        var stop = DrainStopReason.Failed;
        ServiceBusReceiver? receiver = null;
        var lastProgressById = new Dictionary<string, int>(StringComparer.Ordinal);
        var started = _timeProvider.GetUtcNow();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(DrainTimeLimit);
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (!await EntityExistsViaRepoAsync(clean, deadline.Token, throwOnFailure: true))
                return new(0, DrainStopReason.EntityNotFound);
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (progress.Count >= DrainMessageLimit) { stop = DrainStopReason.MessageLimit; break; }
                if (_timeProvider.GetUtcNow() - started >= DrainTimeLimit) { stop = DrainStopReason.TimeLimit; break; }
                receiver = await CreateReceiveReceiverAsync(clean, new(SessionReceiveMode.NextAvailable), deadline.Token);
                sessionCount++;
                var id = ((ServiceBusSessionReceiver)receiver).SessionId;
                // A repeated ID without any intervening deletion cannot make progress; release without receiving again.
                if (lastProgressById.TryGetValue(id, out var previousCount) && previousCount == progress.Count)
                    stop = DrainStopReason.NoProgress;
                else
                {
                    lastProgressById[id] = progress.Count;
                    stop = await DrainReceiverCoreAsync(receiver, progress, started, deadline.Token);
                }
                var disposed = await DisposeReceiverBoundedAsync(receiver, clean);
                receiver = null;
                if (!disposed) { stop = DrainStopReason.Failed; break; }
                if (stop != DrainStopReason.Empty) break;
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            stop = cancellationToken.IsCancellationRequested ? DrainStopReason.Cancelled : DrainStopReason.TimeLimit;
        }
        catch (ServiceBusException ex) when (receiver is null && ex.Reason == ServiceBusFailureReason.ServiceTimeout)
        {
            stop = _timeProvider.GetUtcNow() - started >= DrainTimeLimit ? DrainStopReason.TimeLimit : DrainStopReason.SessionUnavailable;
        }
        catch (ServiceBusException ex) when (receiver is null && ex.Reason == ServiceBusFailureReason.SessionCannotBeLocked)
        {
            stop = DrainStopReason.SessionLocked;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "All-session drain failed for '{Entity}' after {Count} returned messages", clean, progress.Count);
            stop = DrainStopReason.Failed;
        }
        finally
        {
            if (receiver is not null && !await DisposeReceiverBoundedAsync(receiver, clean)) stop = DrainStopReason.Failed;
        }
        return new(progress.Count, stop, SessionCount: sessionCount);
    }

    private sealed class DrainProgress
    {
        public int Count { get; set; }
    }

    private async Task<DrainStopReason> DrainReceiverCoreAsync(ServiceBusReceiver receiver, DrainProgress progress,
        DateTimeOffset started, CancellationToken cancellationToken, int maxMessages = DrainMessageLimit)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (progress.Count >= maxMessages) return DrainStopReason.MessageLimit;
            if (_timeProvider.GetUtcNow() - started >= DrainTimeLimit) return DrainStopReason.TimeLimit;
            var batch = await receiver.ReceiveMessagesAsync(Math.Min(DrainBatchSize, maxMessages - progress.Count), ReceiveIdleWait, cancellationToken);
            progress.Count += batch.Count;
            if (batch.Count == 0) return DrainStopReason.Empty;
        }
    }

    private ServiceBusClient Client
    {
        get
        {
            if (_client == null)
            {
                var cs = NormalizeConnectionString(_connectionString);
                if (string.IsNullOrWhiteSpace(cs))
                {
                    var msg = "ServiceBus client cannot be created because the connection string is not configured. " +
                              "Provide a Service Bus connection string (ConnectionStrings__{resourceName}) or set the ASB connection environment variable.";
                    _logger.LogError(msg);
                    throw new InvalidOperationException(msg);
                }

                _client = _clientFactory(cs);
            }
            return _client;
        }
    }

    private string NormalizeConnectionString(string cs)
    {
        if (string.IsNullOrWhiteSpace(cs)) return cs;
        if (cs.StartsWith("Endpoint=", StringComparison.OrdinalIgnoreCase)) return cs;
        // Treat as hostname
        var host = cs.Replace("http://", "").Replace("https://", "");
        var built = $"Endpoint=sb://{host};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";
        _logger.LogInformation("Normalized Service Bus connection string to: {Preview}", built.Length > 80 ? built[..80] + "..." : built);
        return built;
    }

    internal static string CleanEntityName(string entityName)
    {
        if (string.IsNullOrWhiteSpace(entityName))
            return entityName ?? string.Empty;

        var clean = entityName;
        
        // Remove SBEMULATORNS prefix if present
        if (clean.StartsWith("SBEMULATORNS", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean.Substring("SBEMULATORNS".Length);
        }
        
        // Preserve internal and leading '.' characters because many real-world
        // entity names contain periods (e.g. FIM1437.PAYMENTS...). Removing
        // '.' here prevented discovery of such entities. Only trim the
        // explicit separators that are known to be used around the namespace
        // prefixes while keeping '.' intact.
        clean = clean.TrimStart(':', '|', '/', '\\', '-', '_');
        
        // Split on : to remove QUEUE: or TOPIC: prefix
        var parts = clean.Split(new[] { ':', '|' }, 2);
        if (parts.Length == 2 && (parts[0].Equals("QUEUE", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("TOPIC", StringComparison.OrdinalIgnoreCase)))
        {
            clean = parts[1];
        }
        
        // Trim the same set again after removing QUEUE:/TOPIC: prefixes,
        // but do NOT remove '.' so dotted names are preserved.
        clean = clean.TrimStart(':', '|', '/', '\\', '-', '_');
        
        // Check if this is a subscription (contains |)
        // Format: TOPIC-NAME|SUBSCRIPTION-NAME
        // Should become: topic-name/subscriptions/subscription-name
        if (clean.Contains('|'))
        {
            var subParts = clean.Split('|', 2);
            if (subParts.Length == 2)
            {
                var topicName = subParts[0].ToLowerInvariant();
                var subName = subParts[1].ToLowerInvariant();
                
                // Check if subscription name ends with /$DeadLetterQueue
                if (subName.EndsWith("/$deadletterqueue", StringComparison.OrdinalIgnoreCase))
                {
                    // Remove the DLQ suffix, format properly, then add it back
                    subName = subName.Substring(0, subName.Length - "/$deadletterqueue".Length);
                    return $"{topicName}/subscriptions/{subName}/$DeadLetterQueue";
                }
                
                return $"{topicName}/subscriptions/{subName}";
            }
        }
        
        return clean.ToLowerInvariant();
    }

    /// <summary>
    /// Peeks through an ordinary non-locking receiver, optionally filtering an exact-case session ID.
    /// Bounds scanning to 10,000 messages or 30 seconds; limits and failures indicate incomplete results.
    /// Dead-letter subqueues remain entity-wide. No session is accepted and no message is received.
    /// </summary>
    public Task<PeekMessagesResult> PeekMessagesWithSessionAsync(string queueName, int maxMessages = 20,
        string? sessionId = null, CancellationToken cancellationToken = default)
        => PeekBoundedAsync(queueName, maxMessages, sessionId, false, cancellationToken);

    /// <summary>
    /// Briefly locks the next available active session in PeekLock while ordinary paginated peek filters its exact ID.
    /// Acceptance and inspection share a 30-second deadline and 10,000-message scan cap. Never receives or settles.
    /// Dead-letter subqueues use ordinary entity-wide peek without accepting a session.
    /// </summary>
    public Task<PeekMessagesResult> PeekNextAvailableSessionAsync(string queueName, int maxMessages = 20,
        CancellationToken cancellationToken = default)
        => PeekBoundedAsync(queueName, maxMessages, null, true, cancellationToken);

    private async Task<PeekMessagesResult> PeekBoundedAsync(string queueName, int maxMessages, string? sessionId,
        bool nextSession, CancellationToken cancellationToken)
    {
        var clean = CleanEntityName(queueName);
        if (clean.EndsWith("/$deadletterqueue", StringComparison.OrdinalIgnoreCase)) { sessionId = null; nextSession = false; }
        ServiceBusReceiver? sessionReceiver = null;
        string? acceptedId = null;
        var progress = new PeekProgress();
        var messages = progress.Messages;
        var stop = PeekStopReason.Failed;
        ServiceBusReceiver? receiver = null;
        var started = _timeProvider.GetUtcNow();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ScanTimeLimit);
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (sessionId is not null && string.IsNullOrWhiteSpace(sessionId)) return new(messages, PeekStopReason.InvalidSessionId);
            if (maxMessages <= 0 || maxMessages > PeekScanLimit) throw new ArgumentOutOfRangeException(nameof(maxMessages));
            if (!await EntityExistsViaRepoAsync(clean, deadline.Token, throwOnFailure: true)) return new(messages, PeekStopReason.EntityNotFound);
            if (_timeProvider.GetUtcNow() - started >= ScanTimeLimit) return new(messages, PeekStopReason.TimeLimit);
            if (nextSession)
            {
                sessionReceiver = await CreateReceiveReceiverAsync(clean, new(SessionReceiveMode.NextAvailable), deadline.Token, ServiceBusReceiveMode.PeekLock);
                acceptedId = ((ServiceBusSessionReceiver)sessionReceiver).SessionId;
                sessionId = acceptedId;
            }
            receiver = Client.CreateReceiver(clean, new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock });
            stop = await PeekScanCoreAsync(receiver, maxMessages, sessionId, progress, started, deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            stop = cancellationToken.IsCancellationRequested ? PeekStopReason.Cancelled : PeekStopReason.TimeLimit;
        }
        catch (ServiceBusException ex) when (nextSession && sessionReceiver is null && ex.Reason == ServiceBusFailureReason.ServiceTimeout)
        {
            stop = _timeProvider.GetUtcNow() - started >= ScanTimeLimit ? PeekStopReason.TimeLimit : PeekStopReason.SessionUnavailable;
        }
        catch (ServiceBusException ex) when (nextSession && ex.Reason == ServiceBusFailureReason.SessionCannotBeLocked)
        {
            stop = PeekStopReason.SessionLocked;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bounded peek failed for '{Entity}' after {Count} inspected messages", clean, progress.Scanned);
            stop = PeekStopReason.Failed;
        }
        finally
        {
            if (receiver is not null && !await DisposeReceiverBoundedAsync(receiver, clean)) stop = PeekStopReason.Failed;
            if (sessionReceiver is not null && !await DisposeReceiverBoundedAsync(sessionReceiver, clean)) stop = PeekStopReason.Failed;
        }
        return new(messages, stop, progress.Scanned, acceptedId);
    }

    private sealed class PeekProgress
    {
        public List<DisplayedMessage> Messages { get; } = [];
        public int Scanned { get; set; }
    }

    private async Task<PeekStopReason> PeekScanCoreAsync(ServiceBusReceiver receiver, int maxMessages, string? sessionId,
        PeekProgress progress, DateTimeOffset started, CancellationToken cancellationToken)
    {
        long? next = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (progress.Scanned >= PeekScanLimit) return PeekStopReason.MessageLimit;
            if (_timeProvider.GetUtcNow() - started >= ScanTimeLimit) return PeekStopReason.TimeLimit;
            var size = Math.Min(DrainBatchSize, PeekScanLimit - progress.Scanned);
            if (sessionId is null) size = Math.Min(size, maxMessages - progress.Messages.Count);
            var batch = await receiver.PeekMessagesAsync(size, next, cancellationToken);
            progress.Scanned += batch.Count;
            foreach (var message in batch)
            {
                if (sessionId is null || string.Equals(message.SessionId, sessionId, StringComparison.Ordinal))
                    progress.Messages.Add(ConvertToDisplayedMessage(message));
                if (progress.Messages.Count == maxMessages) break;
            }
            if (progress.Messages.Count == maxMessages || batch.Count == 0) return PeekStopReason.Completed;
            // Use the last sequence, not batch length: deferred messages and gaps are valid.
            var last = batch[^1].SequenceNumber;
            if (last == long.MaxValue) return PeekStopReason.Completed;
            var following = last + 1;
            if (next.HasValue && following <= next.Value) throw new InvalidOperationException("Peek pagination did not advance.");
            next = following;
        }
    }

    private async Task<bool> DisposeReceiverBoundedAsync(ServiceBusReceiver receiver, string clean)
    {
        Task? disposal = null;
        try { disposal = receiver.DisposeAsync().AsTask(); await disposal.WaitAsync(CleanupTimeout); return true; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Receiver disposal failed for '{Entity}'", clean);
            if (disposal is not null) _ = ObserveDisposalAsync(disposal);
            return false;
        }
    }

    private async Task ObserveDisposalAsync(Task disposal)
    {
        try { await disposal; }
        catch (Exception ex) { _logger.LogWarning(ex, "Late receiver disposal failed"); }
    }

    /// <summary>Peeks without deleting messages, using an ordinary receiver for Main or DLQ.</summary>
    public async Task<List<Models.DisplayedMessage>> PeekMessagesAsync(string queueName, int maxMessages = 20, CancellationToken cancellationToken = default)
    {
        var clean = CleanEntityName(queueName);
        _logger.LogInformation("Peeking messages from entity '{Entity}' (cleaned: '{Clean}')", queueName, clean);

        // Verify entity exists via repository
        if (!await EntityExistsViaRepoAsync(clean, cancellationToken))
        {
            var errMsg = $"Entity '{clean}' was not found in the Service Bus namespace (from repository).";
            _logger.LogWarning(errMsg);
            throw new InvalidOperationException(errMsg);
        }

        await using var receiver = Client.CreateReceiver(clean, new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock });
        var messages = await receiver.PeekMessagesAsync(maxMessages, cancellationToken: cancellationToken);
        var results = new List<Models.DisplayedMessage>();
        foreach (var m in messages)
        {
            results.Add(ConvertToDisplayedMessage(m));
        }
        _logger.LogInformation("Peeked {Count} message(s) from '{Entity}'", results.Count, clean);
        return results;
    }

    /// <summary>Receives and deletes one ordinary batch, preserving the existing return type.</summary>
    public async Task<List<Models.DisplayedMessage>> ReceiveMessagesAsync(string queueName, int maxMessages = 20, CancellationToken cancellationToken = default)
    {
        var clean = CleanEntityName(queueName);
        _logger.LogInformation("Receiving messages from entity '{Entity}' (cleaned: '{Clean}')", queueName, clean);

        if (!await EntityExistsViaRepoAsync(clean, cancellationToken))
        {
            var errMsg = $"Entity '{clean}' was not found in the Service Bus namespace (from repository).";
            _logger.LogWarning(errMsg);
            throw new InvalidOperationException(errMsg);
        }

        await using var receiver = Client.CreateReceiver(clean, new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete });
        var messages = await receiver.ReceiveMessagesAsync(maxMessages, ReceiveIdleWait, cancellationToken);
        var results = new List<Models.DisplayedMessage>();
        foreach (var m in messages)
        {
            results.Add(ConvertToDisplayedMessage(m));
        }
        _logger.LogInformation("Received and deleted {Count} message(s) from '{Entity}'", results.Count, clean);
        return results;
    }

    /// <summary>
    /// Sends a pre-built ServiceBusMessage to a queue or topic
    /// </summary>
    public async Task SendMessageAsync(string queueName, ServiceBusMessage message)
    {
        var clean = CleanEntityName(queueName);
        _logger.LogInformation("Sending pre-built message to entity '{Entity}' (cleaned: '{Clean}')", queueName, clean);

        if (!await EntityExistsViaRepoAsync(clean))
        {
            var errMsg = $"Entity '{clean}' was not found in the Service Bus namespace (from repository).";
            _logger.LogWarning(errMsg);
            throw new InvalidOperationException(errMsg);
        }

        await using var sender = Client.CreateSender(clean);
        await sender.SendMessageAsync(message);
        _logger.LogInformation("Successfully sent pre-built message to '{Entity}' - MessageId: {MessageId}, BodySize: {Size} bytes, AppProps: {AppPropsCount}", 
            clean, message.MessageId ?? "(none)", message.Body.ToMemory().Length, message.ApplicationProperties?.Count ?? 0);
    }

    public async Task SendMessageAsync(string queueName, string body, Dictionary<string, object>? applicationProperties = null, IDictionary<string, object>? brokerProperties = null)
    {
        var clean = CleanEntityName(queueName);
        _logger.LogInformation("Sending message to entity '{Entity}' (cleaned: '{Clean}')", queueName, clean);

        if (!await EntityExistsViaRepoAsync(clean))
        {
            var errMsg = $"Entity '{clean}' was not found in the Service Bus namespace (from repository).";
            _logger.LogWarning(errMsg);
            throw new InvalidOperationException(errMsg);
        }

        await using var sender = Client.CreateSender(clean);
        var msg = new ServiceBusMessage(Encoding.UTF8.GetBytes(body ?? string.Empty))
        {
            ContentType = "application/json"
        };
        if (applicationProperties != null)
        {
            foreach (var kv in applicationProperties)
                msg.ApplicationProperties[kv.Key] = kv.Value;
        }

        if (brokerProperties != null)
        {
            if (brokerProperties.TryGetValue("SessionId", out var session)) msg.SessionId = session?.ToString();
            if (brokerProperties.TryGetValue("MessageId", out var mid)) msg.MessageId = mid?.ToString();
            if (brokerProperties.TryGetValue("CorrelationId", out var cid)) msg.CorrelationId = cid?.ToString();
            if (brokerProperties.TryGetValue("ContentType", out var ct)) msg.ContentType = ct?.ToString();
            if (brokerProperties.TryGetValue("Subject", out var subject)) msg.Subject = subject?.ToString();
            if (brokerProperties.TryGetValue("To", out var to)) msg.To = to?.ToString();
            if (brokerProperties.TryGetValue("ReplyTo", out var replyTo)) msg.ReplyTo = replyTo?.ToString();
            if (brokerProperties.TryGetValue("ReplyToSessionId", out var rts)) msg.ReplyToSessionId = rts?.ToString();
            if (brokerProperties.TryGetValue("PartitionKey", out var pk)) msg.PartitionKey = pk?.ToString();

            // TimeToLive: accept seconds (numeric) or TimeSpan string
            if (brokerProperties.TryGetValue("TimeToLive", out var ttlObj) && ttlObj != null)
            {
                var ttlStr = ttlObj.ToString();
                if (double.TryParse(ttlStr, out var seconds))
                {
                    msg.TimeToLive = TimeSpan.FromSeconds(seconds);
                }
                else if (TimeSpan.TryParse(ttlStr, out var span))
                {
                    msg.TimeToLive = span;
                }
            }

            // ScheduledEnqueueTime: parse DateTimeOffset
            if (brokerProperties.TryGetValue("ScheduledEnqueueTime", out var schedObj) && schedObj != null)
            {
                var schedStr = schedObj.ToString();
                if (DateTimeOffset.TryParse(schedStr, out var dto))
                {
                    msg.ScheduledEnqueueTime = dto;
                }
                else if (DateTime.TryParse(schedStr, out var dt))
                {
                    msg.ScheduledEnqueueTime = new DateTimeOffset(dt.ToUniversalTime());
                }
            }
        }

        await sender.SendMessageAsync(msg);
        _logger.LogInformation("Successfully sent message to '{Entity}' - MessageId: {MessageId}, BodySize: {Size} bytes, AppProps: {AppPropsCount}, ContentType: {ContentType}", 
            clean, msg.MessageId ?? "(none)", body?.Length ?? 0, applicationProperties?.Count ?? 0, msg.ContentType);
    }    

    internal async Task<bool> EntityExistsViaRepoAsync(string cleanedEntityNameLower, CancellationToken cancellationToken = default, bool throwOnFailure = false)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entities = await _repo.GetEntitiesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogDebug("Repository returned {Count} entities for existence check", entities?.Count ?? 0);
            
            // Check if it ends with /$DeadLetterQueue and strip it for validation
            var isDlq = cleanedEntityNameLower.EndsWith("/$deadletterqueue", StringComparison.OrdinalIgnoreCase);
            var pathWithoutDlq = isDlq 
                ? cleanedEntityNameLower.Substring(0, cleanedEntityNameLower.Length - "/$deadletterqueue".Length)
                : cleanedEntityNameLower;
            
            // Check if it's a subscription path (contains /subscriptions/)
            if (pathWithoutDlq.Contains("/subscriptions/", StringComparison.OrdinalIgnoreCase))
            {
                // Format: topic-name/subscriptions/sub-name
                var subParts = pathWithoutDlq.Split(new[] { "/subscriptions/" }, StringSplitOptions.None);
                if (subParts.Length == 2)
                {
                    var topicName = subParts[0];
                    var subName = subParts[1];
                    
                    // Look for subscription with format TOPIC-NAME|SUB-NAME
                    var expectedFormat = $"{topicName}|{subName}";
                    _logger.LogDebug("Looking for subscription matching '{Expected}'", expectedFormat);
                    
                    foreach (var e in entities)
                    {
                        if (e.EntityType == "Subscription")
                        {
                            var candidate = CleanEntityNameForComparison(e.Name);
                            _logger.LogTrace("Comparing subscription candidate '{Candidate}' to expected '{Expected}'", candidate, expectedFormat);
                            if (string.Equals(candidate, expectedFormat, StringComparison.OrdinalIgnoreCase))
                                return true;
                        }
                    }
                }
                return false;
            }
            
            // For queues and topics, do simple name matching (with DLQ suffix stripped)
            foreach (var e in entities)
            {
                var candidate = CleanEntityNameForComparison(e.Name);
                _logger.LogTrace("Comparing entity candidate '{Candidate}' to '{Target}' (DLQ stripped '{PathWithoutDlq}')", candidate, cleanedEntityNameLower, pathWithoutDlq);
                if (string.Equals(candidate, pathWithoutDlq, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check entity existence via repository");
            if (throwOnFailure) throw;
            return false;
        }
    }

    internal static string CleanEntityNameForComparison(string entityName)
    {
        if (string.IsNullOrWhiteSpace(entityName))
            return string.Empty;
        
        // Just convert to lowercase for comparison, don't transform subscriptions
        return entityName.ToLowerInvariant();
    }

    private static Models.DisplayedMessage ConvertToDisplayedMessage(ServiceBusReceivedMessage m)
    {
        var dm = new Models.DisplayedMessage
        {
            SequenceNumber = m.SequenceNumber,
            MessageId = m.MessageId,
            CorrelationId = m.CorrelationId,
            SessionId = m.SessionId,
            EnqueuedTime = m.EnqueuedTime,
            ExpiresAt = m.ExpiresAt,
            ContentType = m.ContentType,
            Body = m.Body.ToArray() is byte[] b ? System.Text.Encoding.UTF8.GetString(b) : string.Empty
        };

        try
        {
            foreach (var kv in m.ApplicationProperties)
            {
                dm.ApplicationProperties[kv.Key] = kv.Value;
            }
        }
        catch { }

        // Capture all broker properties
        dm.BrokerProperties["MessageId"] = m.MessageId;
        dm.BrokerProperties["CorrelationId"] = m.CorrelationId;
        dm.BrokerProperties["SequenceNumber"] = m.SequenceNumber;
        dm.BrokerProperties["EnqueuedTime"] = m.EnqueuedTime;
        dm.BrokerProperties["EnqueuedSequenceNumber"] = m.EnqueuedSequenceNumber;
        dm.BrokerProperties["ExpiresAt"] = m.ExpiresAt;
        dm.BrokerProperties["SessionId"] = m.SessionId;
        dm.BrokerProperties["ContentType"] = m.ContentType;
        dm.BrokerProperties["Subject"] = m.Subject;
        dm.BrokerProperties["To"] = m.To;
        dm.BrokerProperties["ReplyTo"] = m.ReplyTo;
        dm.BrokerProperties["ReplyToSessionId"] = m.ReplyToSessionId;
        dm.BrokerProperties["PartitionKey"] = m.PartitionKey;
        dm.BrokerProperties["TimeToLive"] = m.TimeToLive;
        dm.BrokerProperties["ScheduledEnqueueTime"] = m.ScheduledEnqueueTime;
        dm.BrokerProperties["DeliveryCount"] = m.DeliveryCount;
        dm.BrokerProperties["LockToken"] = m.LockToken;
        dm.BrokerProperties["LockedUntil"] = m.LockedUntil;
        dm.BrokerProperties["State"] = m.State.ToString();
        dm.BrokerProperties["DeadLetterErrorDescription"] = m.DeadLetterErrorDescription;
        dm.BrokerProperties["DeadLetterReason"] = m.DeadLetterReason;
        dm.BrokerProperties["DeadLetterSource"] = m.DeadLetterSource;

        return dm;
    }

    /// <summary>
    /// Rejects sequence-based settlement: peeked messages have no settlement lock.
    /// Safe dead-lettering requires a received, locked message and its owning receiver.
    /// No repository or broker calls are made.
    /// </summary>
    public Task DeadLetterMessageAsync(string queueName, long sequenceNumber, string? reason = null, string? errorDescription = null, CancellationToken cancellationToken = default)
        => cancellationToken.IsCancellationRequested
            ? Task.FromCanceled(cancellationToken)
            : Task.FromException(new NotSupportedException("Peeked messages cannot be dead-lettered by sequence number. Settlement requires a received, locked message and its owning receiver."));

    /// <summary>
    /// Rejects sequence-based settlement without receiving or abandoning unrelated messages.
    /// A session ID does not supply the message lock required for safe settlement.
    /// </summary>
    public Task<DeadLetterMessageResult> DeadLetterMessageWithSessionAsync(string queueName, long sequenceNumber,
        string? sessionId = null, string? reason = null, string? errorDescription = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new DeadLetterMessageResult(cancellationToken.IsCancellationRequested
            ? DeadLetterStatus.Cancelled
            : DeadLetterStatus.Unsupported));

    public async ValueTask DisposeAsync()
    {
        if (_client != null) await _client.DisposeAsync();
        if (_lockedClient != null && !ReferenceEquals(_lockedClient, _client)) await _lockedClient.DisposeAsync();
    }

    /// <summary>
    /// Gets accurate message counts for an entity by peeking messages.
    /// Returns (activeCount, deadLetterCount) where counts may be capped at maxPeek if there are more messages.
    /// </summary>
    public async Task<(long ActiveCount, long DeadLetterCount)> GetMessageCountsByPeekingAsync(string entityName, int maxPeek = 100)
    {
        var clean = CleanEntityName(entityName);
        
        try
        {
            // Peek main queue/subscription
            long activeCount = 0;
            try
            {
                var receiver = Client.CreateReceiver(clean, new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock });
                var messages = await receiver.PeekMessagesAsync(maxPeek);
                activeCount = messages.Count;
                await receiver.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not peek main queue '{Entity}', count will be 0", clean);
            }

            // Peek DLQ
            long deadLetterCount = 0;
            try
            {
                var dlqPath = $"{clean}/$DeadLetterQueue";
                var dlqReceiver = Client.CreateReceiver(dlqPath, new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock });
                var dlqMessages = await dlqReceiver.PeekMessagesAsync(maxPeek);
                deadLetterCount = dlqMessages.Count;
                await dlqReceiver.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not peek DLQ for '{Entity}', count will be 0", clean);
            }

            return (activeCount, deadLetterCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get message counts by peeking for '{Entity}'", clean);
            return (0, 0);
        }
    }

    /// <summary>
    /// Enriches a list of entities with message counts by peeking.
    /// This allows the repository to focus on entity discovery while this service handles message counting.
    /// </summary>
    public async Task<List<Models.ServiceBusEntityInfo>> EnrichEntitiesWithMessageCountsAsync(List<Models.ServiceBusEntityInfo> entities, int maxPeek = 100)
    {
        foreach (var entity in entities)
        {
            try
            {
                var counts = await GetMessageCountsByPeekingAsync(entity.Name, maxPeek);
                entity.ActiveMessageCount = counts.ActiveCount;
                entity.DeadletterMessageCount = counts.DeadLetterCount;
            }
            catch
            {
                // If peeking fails, counts remain 0
                entity.ActiveMessageCount = 0;
                entity.DeadletterMessageCount = 0;
            }
        }
        
        return entities;
    }
}

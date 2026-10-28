using AspireAsbEmulatorUi.App.Models;
using Azure.Messaging.ServiceBus;

namespace AspireAsbEmulatorUi.App.Services;

/// <summary>
/// Owns the original PeekLock receiver and SDK messages. No settlement scans, receives, retries,
/// renewals or abandons are performed. Closing releases locks; subsequent redelivery can increase delivery counts.
/// </summary>
public sealed class LockedMessageBatch : IAsyncDisposable
{
    private readonly ServiceBusReceiver _receiver;
    private readonly Func<ServiceBusSender>? _senderFactory;
    private ServiceBusSender? _sender;
    private readonly Dictionary<LockedMessageHandle, ServiceBusReceivedMessage> _messages;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly bool _isDlq;
    private readonly TimeSpan _settlementTimeout;
    private readonly TimeSpan _cleanupTimeout;
    private readonly CancellationTokenSource _timerStop = new();
    private readonly object _disposeSync = new();
    private Task? _disposeTask;
    private volatile bool _releaseRequested;
    internal static readonly TimeSpan SettlementTimeout = TimeSpan.FromSeconds(10);

    // Internal SDK/clock/deadline seam keeps production ownership and tests broker-independent.
    internal LockedMessageBatch(ServiceBusReceiver receiver, IReadOnlyList<ServiceBusReceivedMessage> messages,
        Func<ServiceBusReceivedMessage, DisplayedMessage> project, bool isDlq, TimeProvider clock, ILogger logger,
        TimeSpan? settlementTimeout = null, TimeSpan? cleanupTimeout = null,
        string? replayDestination = null, bool replayToTopic = false, Func<ServiceBusSender>? senderFactory = null)
    {
        _receiver = receiver;
        ReplayDestination = isDlq && !string.IsNullOrWhiteSpace(replayDestination) ? replayDestination : null;
        ReplayToTopic = ReplayDestination is not null && replayToTopic;
        _senderFactory = ReplayDestination is null ? null : senderFactory;
        _clock = clock;
        _logger = logger;
        _isDlq = isDlq;
        _settlementTimeout = settlementTimeout ?? SettlementTimeout;
        _cleanupTimeout = cleanupTimeout ?? ServiceBusService.CleanupTimeout;
        // The SDK session lock covers all messages in the session (including after renewal).
        // Missing/default deadlines are deliberately treated as expired rather than inventing a TTL.
        var sessionDeadline = (receiver as ServiceBusSessionReceiver)?.SessionLockedUntil;
        var entries = messages.Select(m => new LockedMessageEntry(project(m), sessionDeadline ?? m.LockedUntil)).ToArray();
        Entries = Array.AsReadOnly(entries);
        _messages = entries.Zip(messages).ToDictionary(pair => pair.First.Handle, pair => pair.Second);
        MarkExpired();
        _ = WatchExpiryAsync(); // Catches all failures internally; no unobserved timer tasks.
    }

    /// <summary>Whether this batch was acquired from a dead-letter subqueue, never a Main target.</summary>
    public bool IsDeadLetterQueue => _isDlq;

    /// <summary>Display entries; SDK message identity remains private to this batch.</summary>
    public IReadOnlyList<LockedMessageEntry> Entries { get; }

    /// <summary>Whether a handle belongs to this open batch and is still within its effective lock.</summary>
    public bool CanSettle(LockedMessageHandle handle)
        => !_releaseRequested && Entries.Any(e => ReferenceEquals(e.Handle, handle) &&
            e.State == LockedMessageState.Locked && e.LockedUntil > _clock.GetUtcNow());

    /// <summary>Immutable Main queue or parent topic captured when this DLQ batch was acquired.</summary>
    public string? ReplayDestination { get; }

    /// <summary>Whether Replay fans out through the parent topic rather than returning to one queue.</summary>
    public bool ReplayToTopic { get; }

    /// <summary>Whether this owned, unexpired DLQ handle can send a new copy exactly once.</summary>
    public bool CanReplay(LockedMessageHandle handle)
        => _isDlq && ReplayDestination is not null && _senderFactory is not null && CanSettle(handle);

    /// <summary>
    /// Sends one raw copy with a fresh identity, then completes only its original DLQ message after confirmed send.
    /// This is non-atomic: ambiguous send or failed completion is terminal and must not be replayed automatically.
    /// </summary>
    public async Task<MessageReplayResult> ReplayAsync(LockedMessageHandle handle, string freshMessageId,
        CancellationToken cancellationToken = default)
    {
        MessageReplayResult Result(MessageReplaySendStatus send, MessageSettlementStatus? completion = null)
            => new(send, completion, ReplayDestination, freshMessageId);
        if (_releaseRequested) return Result(MessageReplaySendStatus.NotActionable);
        using var gateDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        gateDeadline.CancelAfter(_settlementTimeout);
        try { await _gate.WaitAsync(gateDeadline.Token); }
        catch (OperationCanceledException)
        {
            return Result(cancellationToken.IsCancellationRequested ? MessageReplaySendStatus.Cancelled : MessageReplaySendStatus.NotActionable);
        }
        var releaseGate = true;
        try
        {
            MarkExpired();
            var entry = Entries.FirstOrDefault(e => ReferenceEquals(e.Handle, handle));
            if (entry is null) return Result(MessageReplaySendStatus.InvalidHandle);
            if (!_isDlq || ReplayDestination is null || _senderFactory is null) return Result(MessageReplaySendStatus.Unsupported);
            if (!CanReplay(handle)) return Result(MessageReplaySendStatus.NotActionable);
            if (cancellationToken.IsCancellationRequested) return Result(MessageReplaySendStatus.Cancelled);
            var original = _messages[handle];
            if (string.IsNullOrWhiteSpace(freshMessageId) || freshMessageId.Length > 128 || freshMessageId == original.MessageId)
                return Result(MessageReplaySendStatus.NotSent);
            ServiceBusMessage copy;
            try
            {
                copy = ServiceBusService.CreateReplayMessage(original, freshMessageId);
                _sender ??= _senderFactory();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Replay preparation failed before dispatch");
                return Result(MessageReplaySendStatus.NotSent);
            }
            // Preparation can take time or a caller can cancel/dispose while the sender is created.
            if (cancellationToken.IsCancellationRequested) return Result(MessageReplaySendStatus.Cancelled);
            if (_releaseRequested || entry.LockedUntil <= _clock.GetUtcNow())
            {
                MarkExpired();
                return Result(MessageReplaySendStatus.NotActionable);
            }
            entry.State = LockedMessageState.Settling;
            Task? operation = null;
            using (var sendDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                sendDeadline.CancelAfter(_settlementTimeout);
                try
                {
                    operation = _sender.SendMessageAsync(copy, sendDeadline.Token);
                    await operation.WaitAsync(sendDeadline.Token);
                }
                catch (Exception ex)
                {
                    entry.State = LockedMessageState.Unknown;
                    _logger.LogWarning(ex, "Replay send was not confirmed; original not completed and this handle must not be retried");
                    if (operation is { IsCompleted: false })
                    {
                        releaseGate = false;
                        _ = FinishLateSettlementAsync(operation);
                    }
                    else if (operation is not null) ObserveCompleted(operation);
                    return Result(MessageReplaySendStatus.Unknown);
                }
            }
            // Confirmation is durable local progress: cleanup or completion failures cannot turn it into NotSent.
            if (entry.LockedUntil <= _clock.GetUtcNow())
            {
                entry.State = LockedMessageState.Expired;
                return Result(MessageReplaySendStatus.Confirmed, MessageSettlementStatus.NotActionable);
            }
            if (_releaseRequested || cancellationToken.IsCancellationRequested)
            {
                entry.State = LockedMessageState.Unknown;
                return Result(MessageReplaySendStatus.Confirmed, cancellationToken.IsCancellationRequested
                    ? MessageSettlementStatus.Cancelled : MessageSettlementStatus.NotActionable);
            }
            using var completeDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            completeDeadline.CancelAfter(_settlementTimeout);
            operation = null;
            try
            {
                // Never call public CompleteAsync while owning the batch gate.
                operation = _receiver.CompleteMessageAsync(original, completeDeadline.Token);
                await operation.WaitAsync(completeDeadline.Token);
                entry.State = LockedMessageState.Completed;
                return Result(MessageReplaySendStatus.Confirmed, MessageSettlementStatus.Completed);
            }
            catch (ServiceBusException ex) when (ex.Reason is ServiceBusFailureReason.MessageLockLost or ServiceBusFailureReason.SessionLockLost)
            {
                MarkLockLost(entry, ex.Reason);
                return Result(MessageReplaySendStatus.Confirmed, MessageSettlementStatus.LockLost);
            }
            catch (Exception ex)
            {
                entry.State = LockedMessageState.Unknown;
                _logger.LogWarning(ex, "Replay copy was sent but original completion was not confirmed; do not resend");
                if (operation is { IsCompleted: false })
                {
                    releaseGate = false;
                    _ = FinishLateSettlementAsync(operation);
                }
                else if (operation is not null) ObserveCompleted(operation);
                return Result(MessageReplaySendStatus.Confirmed, MessageSettlementStatus.Unknown);
            }
        }
        finally
        {
            if (releaseGate) _gate.Release();
            if (Entries.All(e => e.State != LockedMessageState.Locked && e.State != LockedMessageState.Settling))
                _ = DisposeAsync().AsTask();
        }
    }

    private void MarkLockLost(LockedMessageEntry entry, ServiceBusFailureReason reason)
    {
        if (reason == ServiceBusFailureReason.SessionLockLost)
            foreach (var item in Entries.Where(e => e.State is LockedMessageState.Locked or LockedMessageState.Settling))
                item.State = LockedMessageState.LockLost;
        else entry.State = LockedMessageState.LockLost;
    }

    /// <summary>Completes exactly the retained message through its owning receiver, once.</summary>
    public Task<MessageSettlementResult> CompleteAsync(LockedMessageHandle handle, CancellationToken cancellationToken = default)
        => SettleAsync(handle, false, null, null, cancellationToken);

    /// <summary>Dead-letters exactly the retained Main message; a DLQ message cannot be dead-lettered again.</summary>
    public Task<MessageSettlementResult> DeadLetterAsync(LockedMessageHandle handle, string? reason = null,
        string? errorDescription = null, CancellationToken cancellationToken = default)
        => SettleAsync(handle, true, reason, errorDescription, cancellationToken);

    private async Task<MessageSettlementResult> SettleAsync(LockedMessageHandle handle, bool deadLetter,
        string? reason, string? description, CancellationToken token)
    {
        if (_releaseRequested) return new(MessageSettlementStatus.NotActionable);
        using var gateDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        gateDeadline.CancelAfter(_settlementTimeout);
        try { await _gate.WaitAsync(gateDeadline.Token); }
        catch (OperationCanceledException)
        {
            return new(token.IsCancellationRequested ? MessageSettlementStatus.Cancelled : MessageSettlementStatus.NotActionable);
        }
        var releaseGate = true;
        try
        {
            MarkExpired();
            var entry = Entries.FirstOrDefault(e => ReferenceEquals(e.Handle, handle));
            if (entry is null) return new(MessageSettlementStatus.InvalidHandle);
            if (deadLetter && _isDlq) return new(MessageSettlementStatus.Unsupported);
            if (!CanSettle(handle)) return new(MessageSettlementStatus.NotActionable);
            if (token.IsCancellationRequested) return new(MessageSettlementStatus.Cancelled);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(_settlementTimeout);
            entry.State = LockedMessageState.Settling;
            Task? operation = null;
            try
            {
                operation = deadLetter
                    ? _receiver.DeadLetterMessageAsync(_messages[handle], reason ?? "Manually dead-lettered", description, deadline.Token)
                    : _receiver.CompleteMessageAsync(_messages[handle], deadline.Token);
                await operation.WaitAsync(deadline.Token);
                entry.State = deadLetter ? LockedMessageState.DeadLettered : LockedMessageState.Completed;
                return new(deadLetter ? MessageSettlementStatus.DeadLettered : MessageSettlementStatus.Completed);
            }
            catch (ServiceBusException ex) when (ex.Reason is ServiceBusFailureReason.MessageLockLost or ServiceBusFailureReason.SessionLockLost)
            {
                MarkLockLost(entry, ex.Reason);
                return new(MessageSettlementStatus.LockLost);
            }
            catch (Exception ex)
            {
                entry.State = LockedMessageState.Unknown;
                _logger.LogWarning(ex, "Locked message settlement was not confirmed; do not retry this handle");
                if (operation is { IsCompleted: false })
                {
                    // A non-cooperative SDK task still owns the receiver. Observe its eventual outcome
                    // before releasing the gate: cleanup must NEVER close concurrently with settlement.
                    releaseGate = false;
                    _ = FinishLateSettlementAsync(operation);
                }
                else if (operation is not null) ObserveCompleted(operation);
                return new(MessageSettlementStatus.Unknown);
            }
        }
        finally
        {
            if (releaseGate) _gate.Release();
            if (Entries.All(e => e.State != LockedMessageState.Locked && e.State != LockedMessageState.Settling))
                _ = DisposeAsync().AsTask();
        }
    }

    private async Task FinishLateSettlementAsync(Task operation)
    {
        try { await operation; }
        catch (Exception ex) { _logger.LogWarning(ex, "Late settlement ended without confirmation"); }
        finally { _gate.Release(); }
    }

    private static void ObserveCompleted(Task operation) { _ = operation.Exception; }

    private void MarkExpired()
    {
        var now = _clock.GetUtcNow();
        foreach (var entry in Entries)
            if (entry.State == LockedMessageState.Locked && entry.LockedUntil <= now)
                entry.State = LockedMessageState.Expired;
    }

    /// <summary>Refreshes local expiry without broker calls or renewal, closing once every entry is terminal.</summary>
    public async Task RefreshExpiryAsync(CancellationToken cancellationToken = default)
    {
        if (_releaseRequested) return;
        // A late non-cooperative SDK task may still hold the gate. UI expiry refresh must not wait for it.
        if (!await _gate.WaitAsync(0, cancellationToken)) return;
        bool terminal;
        try { MarkExpired(); terminal = Entries.All(e => e.State != LockedMessageState.Locked && e.State != LockedMessageState.Settling); }
        finally { _gate.Release(); }
        if (terminal) await DisposeAsync();
    }

    private async Task WatchExpiryAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _clock);
            while (await timer.WaitForNextTickAsync(_timerStop.Token))
                await RefreshExpiryAsync(_timerStop.Token);
        }
        catch (OperationCanceledException) when (_timerStop.IsCancellationRequested) { }
        catch (Exception ex) { _logger.LogWarning(ex, "Locked batch expiry monitor stopped"); }
    }

    /// <summary>
    /// Invalidates actions immediately and waits a bounded time for serialized receiver disposal.
    /// Outstanding messages are never abandoned; if an SDK task hangs, broker expiry is the backstop.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Task cleanup;
        lock (_disposeSync)
        {
            _releaseRequested = true;
            _timerStop.Cancel();
            cleanup = _disposeTask ??= CleanupAsync();
        }
        try { await cleanup.WaitAsync(_cleanupTimeout); }
        catch (TimeoutException) { _logger.LogWarning("Locked batch cleanup exceeded its wait budget; broker lock expiry remains the backstop"); }
    }

    private async Task CleanupAsync()
    {
        await _gate.WaitAsync();
        try
        {
            MarkExpired();
            foreach (var entry in Entries.Where(e => e.State == LockedMessageState.Locked)) entry.State = LockedMessageState.Released;
            await _receiver.DisposeAsync();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Locked batch receiver cleanup failed"); }
        finally
        {
            try
            {
                if (_sender is not null) await _sender.DisposeAsync();
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Locked batch Replay sender cleanup failed"); }
            finally { _gate.Release(); }
        }
    }
}

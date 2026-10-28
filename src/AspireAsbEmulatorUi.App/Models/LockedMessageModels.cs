using AspireAsbEmulatorUi.App.Services;

namespace AspireAsbEmulatorUi.App.Models;

/// <summary>A viewer-owned receiver and its locked messages, if acquisition succeeded.</summary>
public sealed record LockedReceiveResult(LockedMessageBatch? Batch, MessageOperationStatus Status, string? AcceptedSessionId = null);

/// <summary>An opaque reference valid only within its original batch; never a sequence-based lookup.</summary>
public sealed class LockedMessageHandle
{
    internal LockedMessageHandle() { }
}

/// <summary>The local settlement lifecycle; Unknown must not be retried.</summary>
public enum LockedMessageState { Locked, Settling, Completed, DeadLettered, Expired, LockLost, Unknown, Released }

/// <summary>A display projection and its effective broker lock deadline, without exposing the SDK message.</summary>
public sealed class LockedMessageEntry
{
    internal LockedMessageEntry(DisplayedMessage message, DateTimeOffset lockedUntil)
    {
        Message = message;
        LockedUntil = lockedUntil;
    }
    /// <summary>Reference to the retained message within the owning batch.</summary>
    public LockedMessageHandle Handle { get; } = new();
    /// <summary>Display-only message projection.</summary>
    public DisplayedMessage Message { get; }
    /// <summary>Session lock deadline for session messages, otherwise the individual message lock deadline.</summary>
    public DateTimeOffset LockedUntil { get; }
    /// <summary>Current local state; expiry and SDK lock-loss always disable further settlement.</summary>
    public LockedMessageState State { get; internal set; } = LockedMessageState.Locked;
}

/// <summary>Confirmed successes are distinct from cancellation before dispatch and ambiguous failures after dispatch.</summary>
public enum MessageSettlementStatus { Completed, DeadLettered, InvalidHandle, NotActionable, Cancelled, LockLost, Unknown, Unsupported }

/// <summary>Replay send phase; Unknown is dispatched but unconfirmed and must never be retried.</summary>
public enum MessageReplaySendStatus { NotSent, Cancelled, Unsupported, InvalidHandle, NotActionable, Unknown, Confirmed }

/// <summary>Independent send and original-message completion outcomes for a non-atomic Replay.</summary>
public sealed record MessageReplayResult(MessageReplaySendStatus SendStatus, MessageSettlementStatus? CompletionStatus = null,
    string? Destination = null, string? MessageId = null)
{
    /// <summary>Whether the SDK confirmed the new copy was sent, regardless of cleanup or completion failures.</summary>
    public bool IsSendConfirmed => SendStatus == MessageReplaySendStatus.Confirmed;
    /// <summary>Whether the original DLQ message was confirmed completed after sending.</summary>
    public bool IsCompleted => IsSendConfirmed && CompletionStatus == MessageSettlementStatus.Completed;
}

/// <summary>Outcome of one target-only, non-retried settlement.</summary>
public sealed record MessageSettlementResult(MessageSettlementStatus Status)
{
    /// <summary>Whether the SDK confirmed the requested settlement.</summary>
    public bool IsSettled => Status is MessageSettlementStatus.Completed or MessageSettlementStatus.DeadLettered;
}

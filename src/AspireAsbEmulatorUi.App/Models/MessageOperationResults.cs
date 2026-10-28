namespace AspireAsbEmulatorUi.App.Models;

/// <summary>Explicit receiver selection; dead-letter subqueues use Standard.</summary>
public enum SessionReceiveMode { Standard, NextAvailable, Specified }

/// <summary>Selects one session, preserving the supplied session ID exactly.</summary>
public sealed record SessionReceiveOptions(SessionReceiveMode Mode = SessionReceiveMode.Standard, string? SessionId = null);

/// <summary>Safe outcomes for a single receive operation.</summary>
public enum MessageOperationStatus { Completed, EntityNotFound, InvalidSessionId, SessionUnavailable, SessionLocked, Cancelled, Failed }

/// <summary>Messages returned by the SDK and the ID of the one accepted session, if any.</summary>
public sealed record ReceiveMessagesResult(IReadOnlyList<DisplayedMessage> Messages, MessageOperationStatus Status, string? AcceptedSessionId = null);

/// <summary>The reason a bounded destructive drain stopped; Empty is not a guaranteed purge.</summary>
public enum DrainStopReason { Empty, MessageLimit, TimeLimit, Cancelled, EntityNotFound, InvalidSessionId, SessionUnavailable, SessionLocked, Failed, NoProgress }

/// <summary>Counts only messages returned by the SDK; transport failures may leave actual deletions uncertain.</summary>
public sealed record DrainMessagesResult(int DeletedCount, DrainStopReason StopReason, string? AcceptedSessionId = null, int SessionCount = 0);

/// <summary>Completed means the requested count was found or the peek reached the end; limits are incomplete.</summary>
public enum PeekStopReason { Completed, MessageLimit, TimeLimit, Cancelled, EntityNotFound, InvalidSessionId, Failed, SessionUnavailable, SessionLocked }

/// <summary>Peek results, number inspected across all sessions, and the next-session ID if a session was accepted.</summary>
public sealed record PeekMessagesResult(IReadOnlyList<DisplayedMessage> Messages, PeekStopReason StopReason, int ScannedCount = 0, string? AcceptedSessionId = null)
{
    /// <summary>Whether the requested view completed without a limit or error.</summary>
    public bool IsComplete => StopReason == PeekStopReason.Completed;
}

/// <summary>Legacy settlement outcomes; sequence-based requests now return Unsupported or Cancelled without broker calls.</summary>
public enum DeadLetterStatus { Completed, NotFound, MessageLimit, TimeLimit, Cancelled, EntityNotFound, InvalidSessionId, SessionUnavailable, SessionLocked, Failed, Unsupported }

/// <summary>Settlement outcome independent of best-effort non-target lock cleanup and receiver disposal.</summary>
public sealed record DeadLetterMessageResult(DeadLetterStatus Status, bool CleanupFailed = false)
{
    /// <summary>Whether the SDK confirmed settlement, even if subsequent cleanup failed.</summary>
    public bool IsSettled => Status == DeadLetterStatus.Completed;
}

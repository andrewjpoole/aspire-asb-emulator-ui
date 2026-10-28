using AspireAsbEmulatorUi.App.Models;

namespace AspireAsbEmulatorUi.App.Services;

/// <summary>Completes retained DLQ messages before draining available messages within one shared budget.</summary>
public static class ReceivedDlqClearOperation
{
    /// <summary>
    /// Completes only original owned DLQ handles, then calls the drain with the remaining 10,000-message
    /// budget and shared 30-second cancellation deadline. Unconfirmed owned completions stop the operation.
    /// Previously settled handles are skipped, not counted again. The caller owns batch cleanup and the
    /// callback must honor its supplied count and token; Empty is never a guaranteed purge.
    /// </summary>
    public static Task<DrainMessagesResult> ClearAsync(LockedMessageBatch batch,
        Func<int, CancellationToken, Task<DrainMessagesResult>> drain, CancellationToken token = default)
        => ClearAsync(batch, drain, token, ServiceBusService.DrainTimeLimit);

    // Real linked-token deadline with a shorter duration seam for deterministic, broker-independent tests.
    internal static async Task<DrainMessagesResult> ClearAsync(LockedMessageBatch batch,
        Func<int, CancellationToken, Task<DrainMessagesResult>> drain, CancellationToken token, TimeSpan timeLimit)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(drain);
        if (!batch.IsDeadLetterQueue) return new(0, DrainStopReason.Failed);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeLimit);
        var count = 0;
        var unresolved = false;
        DrainStopReason CancellationReason() => token.IsCancellationRequested ? DrainStopReason.Cancelled : DrainStopReason.TimeLimit;
        try
        {
            foreach (var entry in batch.Entries)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (count >= ServiceBusService.DrainMessageLimit) return new(count, DrainStopReason.MessageLimit);
                if (entry.State is LockedMessageState.Completed or LockedMessageState.DeadLettered) continue;
                if (!batch.CanSettle(entry.Handle))
                {
                    // Expired, Unknown, released or concurrently settling locks may still exist at the broker.
                    unresolved = true;
                    continue;
                }
                var completed = await batch.CompleteAsync(entry.Handle, deadline.Token);
                if (completed.Status != MessageSettlementStatus.Completed)
                    return new(count, deadline.IsCancellationRequested ? CancellationReason() : DrainStopReason.Failed);
                count++;
            }
            deadline.Token.ThrowIfCancellationRequested();
            if (count >= ServiceBusService.DrainMessageLimit) return new(count, DrainStopReason.MessageLimit);
            if (unresolved) return new(count, DrainStopReason.Failed);
            var result = await drain(ServiceBusService.DrainMessageLimit - count, deadline.Token);
            return result with
            {
                DeletedCount = count + result.DeletedCount,
                StopReason = deadline.IsCancellationRequested ? CancellationReason() : result.StopReason
            };
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return new(count, CancellationReason());
        }
        catch (Exception)
        {
            // No retry: only the retained confirmations are certain if the callback failed without a result.
            return new(count, DrainStopReason.Failed);
        }
    }
}

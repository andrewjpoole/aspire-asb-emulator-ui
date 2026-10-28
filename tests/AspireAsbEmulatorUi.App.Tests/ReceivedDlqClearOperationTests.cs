using AspireAsbEmulatorUi.App.Models;
using AspireAsbEmulatorUi.App.Services;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;

namespace AspireAsbEmulatorUi.App.Tests;

public class ReceivedDlqClearOperationTests
{
    [Test]
    public async Task Completes_Original_Dlq_Handles_Before_Drain_Without_Receive_Or_Abandon()
    {
        var receiver = new FakeReceiver();
        var originals = Messages(3);
        await using var batch = CreateBatch(receiver, originals);
        var calls = 0;
        var result = await ReceivedDlqClearOperation.ClearAsync(batch, async (remaining, token) =>
        {
            calls++;
            await Assert.That(receiver.CompletedMessages.SequenceEqual(originals)).IsTrue();
            await Assert.That(remaining).IsEqualTo(9_997);
            await Assert.That(token.CanBeCanceled).IsTrue();
            return new(4, DrainStopReason.Empty);
        });
        await Assert.That(result).IsEqualTo(new DrainMessagesResult(7, DrainStopReason.Empty));
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(receiver.BatchSizes.Count).IsEqualTo(0);
        await Assert.That(receiver.PeekCalls.Count).IsEqualTo(0);
        await Assert.That(receiver.Abandoned.Count).IsEqualTo(0);
        await Assert.That(receiver.DeadLetteredMessages.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Main_Batch_Is_Rejected_Without_Broker_Or_Drain_Calls()
    {
        var receiver = new FakeReceiver();
        await using var batch = CreateBatch(receiver, Messages(1), false);
        var calls = 0;
        var result = await ReceivedDlqClearOperation.ClearAsync(batch, (_, _) =>
        {
            calls++;
            return Task.FromResult(new DrainMessagesResult(9, DrainStopReason.Empty));
        });
        await Assert.That(result).IsEqualTo(new DrainMessagesResult(0, DrainStopReason.Failed));
        await Assert.That(receiver.CompletedMessages.Count).IsEqualTo(0);
        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(batch.CanSettle(batch.Entries[0].Handle)).IsTrue();
    }

    [Test]
    public async Task Previously_Completed_Handles_Are_Not_Recounted_Or_Retried()
    {
        var receiver = new FakeReceiver();
        await using var batch = CreateBatch(receiver, Messages(3));
        await batch.CompleteAsync(batch.Entries[0].Handle);
        var result = await ReceivedDlqClearOperation.ClearAsync(batch,
            (_, _) => Task.FromResult(new DrainMessagesResult(1, DrainStopReason.Empty)));
        await Assert.That(result.DeletedCount).IsEqualTo(3);
        await Assert.That(receiver.CompletedMessages.Count).IsEqualTo(3);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Completion_Failure_Or_Cancellation_Stops_With_Confirmed_Progress(bool cancel)
    {
        using var cts = new CancellationTokenSource();
        var receiver = new FakeReceiver();
        receiver.Settle = (_, token) =>
        {
            if (receiver.CompletedMessages.Count == 1) return Task.CompletedTask;
            if (cancel) { cts.Cancel(); return Task.FromCanceled(token); }
            return Task.FromException(new InvalidOperationException("unconfirmed"));
        };
        await using var batch = CreateBatch(receiver, Messages(3));
        var drainCalls = 0;
        var result = await ReceivedDlqClearOperation.ClearAsync(batch, (_, _) =>
        {
            drainCalls++;
            return Task.FromResult(new DrainMessagesResult(8, DrainStopReason.Empty));
        }, cts.Token);
        await Assert.That(result).IsEqualTo(new DrainMessagesResult(1, cancel ? DrainStopReason.Cancelled : DrainStopReason.Failed));
        await Assert.That(receiver.CompletedMessages.Count).IsEqualTo(2);
        await Assert.That(batch.Entries[2].State).IsEqualTo(LockedMessageState.Locked);
        await Assert.That(drainCalls).IsEqualTo(0);
    }

    [Test]
    [Arguments(LockedMessageState.Expired)]
    [Arguments(LockedMessageState.Unknown)]
    [Arguments(LockedMessageState.LockLost)]
    [Arguments(LockedMessageState.Released)]
    public async Task Unresolved_Owned_Entries_Never_Report_Empty(LockedMessageState state)
    {
        var receiver = new FakeReceiver();
        await using var batch = CreateBatch(receiver, Messages(2));
        batch.Entries[0].State = state;
        var drainCalls = 0;
        var result = await ReceivedDlqClearOperation.ClearAsync(batch, (_, _) =>
        {
            drainCalls++;
            return Task.FromResult(new DrainMessagesResult(0, DrainStopReason.Empty));
        });
        await Assert.That(result).IsEqualTo(new DrainMessagesResult(1, DrainStopReason.Failed));
        await Assert.That(receiver.CompletedMessages.Count).IsEqualTo(1);
        await Assert.That(drainCalls).IsEqualTo(0);
    }

    [Test]
    public async Task Closed_Batch_Is_Not_Settled_Or_Drained()
    {
        var receiver = new FakeReceiver();
        await using var batch = CreateBatch(receiver, Messages(2));
        await batch.DisposeAsync();
        var calls = 0;
        var result = await ReceivedDlqClearOperation.ClearAsync(batch, (_, _) =>
        {
            calls++;
            return Task.FromResult(new DrainMessagesResult(0, DrainStopReason.Empty));
        });
        await Assert.That(result.StopReason).IsEqualTo(DrainStopReason.Failed);
        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(receiver.CompletedMessages.Count).IsEqualTo(0);
    }

    [Test]
    public async Task PreCancelled_Clear_Does_Not_Mutate_Batch()
    {
        var receiver = new FakeReceiver();
        await using var batch = CreateBatch(receiver, Messages(1));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await ReceivedDlqClearOperation.ClearAsync(batch,
            (_, _) => throw new InvalidOperationException("must not drain"), cts.Token);
        await Assert.That(result).IsEqualTo(new DrainMessagesResult(0, DrainStopReason.Cancelled));
        await Assert.That(receiver.CompletedMessages.Count).IsEqualTo(0);
        await Assert.That(batch.CanSettle(batch.Entries[0].Handle)).IsTrue();
    }

    [Test]
    public async Task Global_Message_Budget_Includes_Retained_Completions()
    {
        var receiver = new FakeReceiver();
        await using var batch = CreateBatch(receiver, Messages(10_001));
        var result = await ReceivedDlqClearOperation.ClearAsync(batch,
            (_, _) => throw new InvalidOperationException("must not drain"));
        await Assert.That(result).IsEqualTo(new DrainMessagesResult(10_000, DrainStopReason.MessageLimit));
        await Assert.That(receiver.CompletedMessages.Count).IsEqualTo(10_000);
        await Assert.That(batch.Entries.Last().State).IsEqualTo(LockedMessageState.Locked);
    }

    [Test]
    [Arguments(DrainStopReason.Cancelled)]
    [Arguments(DrainStopReason.TimeLimit)]
    [Arguments(DrainStopReason.Failed)]
    [Arguments(DrainStopReason.MessageLimit)]
    public async Task Drain_Stop_And_Partial_Count_Are_Preserved(DrainStopReason stop)
    {
        await using var batch = CreateBatch(new(), Messages(1));
        var result = await ReceivedDlqClearOperation.ClearAsync(batch,
            (_, _) => Task.FromResult(new DrainMessagesResult(3, stop)));
        await Assert.That(result).IsEqualTo(new DrainMessagesResult(4, stop));
    }

    [Test]
    public async Task One_Deadline_Covers_Completion_And_Drain()
    {
        var receiver = new FakeReceiver { Settle = async (_, token) => await Task.Delay(70, token) };
        await using var batch = CreateBatch(receiver, Messages(1));
        var calls = 0;
        var result = await ReceivedDlqClearOperation.ClearAsync(batch, async (_, token) =>
        {
            calls++;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(0, DrainStopReason.Empty);
        }, default, TimeSpan.FromMilliseconds(180));
        await Assert.That(result).IsEqualTo(new DrainMessagesResult(1, DrainStopReason.TimeLimit));
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(receiver.CompletedMessages.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Global_Deadline_Stops_Completion_Before_Drain_Without_Concurrent_Cleanup()
    {
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = new FakeReceiver { Settle = (_, _) => finish.Task };
        await using var batch = CreateBatch(receiver, Messages(2));
        var result = await ReceivedDlqClearOperation.ClearAsync(batch,
            (_, _) => throw new InvalidOperationException("must not drain"), default, TimeSpan.FromMilliseconds(80));
        await Assert.That(result).IsEqualTo(new DrainMessagesResult(0, DrainStopReason.TimeLimit));
        await Assert.That(batch.Entries[0].State).IsEqualTo(LockedMessageState.Unknown);
        await Assert.That(receiver.CompletedMessages.Count).IsEqualTo(1);
        await Assert.That(receiver.Disposed).IsFalse();
        finish.SetResult();
    }

    [Test]
    public async Task Cancelled_Drain_Preserves_Its_Returned_Partial_Progress()
    {
        using var cts = new CancellationTokenSource();
        await using var batch = CreateBatch(new(), Messages(1));
        var result = await ReceivedDlqClearOperation.ClearAsync(batch, (_, _) =>
        {
            cts.Cancel();
            return Task.FromResult(new DrainMessagesResult(5, DrainStopReason.Cancelled));
        }, cts.Token);
        await Assert.That(result).IsEqualTo(new DrainMessagesResult(6, DrainStopReason.Cancelled));
    }

    [Test]
    public async Task Drain_Exception_Does_Not_Lose_Retained_Count()
    {
        await using var batch = CreateBatch(new(), Messages(1));
        var result = await ReceivedDlqClearOperation.ClearAsync(batch,
            (_, _) => throw new InvalidOperationException("transport failure"));
        await Assert.That(result).IsEqualTo(new DrainMessagesResult(1, DrainStopReason.Failed));
    }

    private static IReadOnlyList<ServiceBusReceivedMessage> Messages(int count)
        => Enumerable.Range(0, count).Select(i => ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString("body"), messageId: $"owned-{i}",
            timeToLive: TimeSpan.FromMinutes(5), lockedUntil: DateTimeOffset.UtcNow.AddMinutes(5))).ToArray();

    private static LockedMessageBatch CreateBatch(FakeReceiver receiver, IReadOnlyList<ServiceBusReceivedMessage> messages, bool dlq = true)
        => new(receiver, messages, m => new DisplayedMessage { MessageId = m.MessageId }, dlq,
            TimeProvider.System, NullLogger.Instance);
}

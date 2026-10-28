using AspireAsbEmulatorUi.App.Models;
using AspireAsbEmulatorUi.App.Services;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;

namespace AspireAsbEmulatorUi.App.Tests;

public class LockedMessageBatchTests
{
    [Test]
    public async Task Receive_Owns_Original_Receiver_And_Settles_Only_Selected_Message()
    {
        var client = new FakeClient();
        var repo = new FakeEntityRepository();
        var originals = Messages(3);
        client.Receiver.Receive = (_, _) => Task.FromResult(originals);
        await using var service = ServiceBusFixture.Create(client, repo);
        var received = await service.ReceiveLockedMessagesAsync("MyQueue");
        await using var batch = received.Batch!;
        await Assert.That(received.Status).IsEqualTo(MessageOperationStatus.Completed);
        await Assert.That(client.Receiver.Disposed).IsFalse();
        var result = await batch.CompleteAsync(batch.Entries[1].Handle);
        await Assert.That(result.IsSettled).IsTrue();
        await Assert.That(ReferenceEquals(client.Receiver.CompletedMessages.Single(), originals[1])).IsTrue();
        await Assert.That(batch.Entries[0].State).IsEqualTo(LockedMessageState.Locked);
        await Assert.That(client.Receiver.Abandoned.Count).IsEqualTo(0);
        await Assert.That(client.Receiver.BatchSizes.Count).IsEqualTo(1);
        await Assert.That(client.SessionCalls.Count).IsEqualTo(0);
        await Assert.That(repo.Calls).IsEqualTo(1);
        await Assert.That(client.Receiver.Disposed).IsFalse();
    }

    [Test]
    [Arguments("MyQueue", "myqueue")]
    [Arguments("MyTopic|MySub", "mytopic/subscriptions/mysub")]
    [Arguments("MyQueue/$DeadLetterQueue", "myqueue/$deadletterqueue")]
    [Arguments("MyTopic|MySub/$DeadLetterQueue", "mytopic/subscriptions/mysub/$DeadLetterQueue")]
    public async Task Ordinary_Routing_Is_PeekLock_ZeroPrefetch(string entity, string path)
    {
        var client = new FakeClient();
        client.Receiver.Receive = (_, _) => Task.FromResult(Messages(1));
        await using var service = ServiceBusFixture.Create(client, new());
        var result = await service.ReceiveLockedMessagesAsync(entity);
        await using var batch = result.Batch!;
        await Assert.That(client.OrdinaryCalls.Single().Path).IsEqualTo(path);
        await Assert.That(client.OrdinaryCalls.Single().Mode).IsEqualTo(ServiceBusReceiveMode.PeekLock);
        await Assert.That(client.PrefetchCounts.Single()).IsEqualTo(0);
        await Assert.That(batch.Entries.Single().Message.Body).IsEqualTo("body");
        await Assert.That(batch.Entries.Single().Message.SessionId).IsEqualTo("CaseSensitiveID");
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task Session_Routing_And_Deadline_Are_Authoritative(bool subscription, bool specified)
    {
        var client = new FakeClient();
        // Individual deadline may be missing/older: the session lock is what protects all messages.
        var originals = Messages(2, DateTimeOffset.MinValue);
        client.SessionReceiver.Receive = (_, _) => Task.FromResult(originals);
        await using var service = ServiceBusFixture.Create(client, new());
        var result = await service.ReceiveLockedMessagesAsync(subscription ? "MyTopic|MySub" : "MyQueue", 5,
            new(specified ? SessionReceiveMode.Specified : SessionReceiveMode.NextAvailable, "CaseSensitiveID"));
        await using var batch = result.Batch!;
        var call = client.SessionCalls.Single();
        await Assert.That(call.QueueOrTopic).IsEqualTo(subscription ? "mytopic" : "myqueue");
        await Assert.That(call.Subscription).IsEqualTo(subscription ? "mysub" : null);
        await Assert.That(call.Id).IsEqualTo(specified ? "CaseSensitiveID" : null);
        await Assert.That(call.Mode).IsEqualTo(ServiceBusReceiveMode.PeekLock);
        await Assert.That(client.PrefetchCounts.Single()).IsEqualTo(0);
        await Assert.That(result.AcceptedSessionId).IsEqualTo("CaseSensitiveID");
        await Assert.That(batch.Entries[0].LockedUntil).IsEqualTo(client.SessionReceiver.LockDeadline);
        var settled = await batch.DeadLetterAsync(batch.Entries[0].Handle, "reason", "description");
        await Assert.That(settled.Status).IsEqualTo(MessageSettlementStatus.DeadLettered);
        await Assert.That(ReferenceEquals(client.SessionReceiver.Settlement.DeadLetteredMessages.Single(), originals[0])).IsTrue();
        await Assert.That(client.SessionReceiver.Settlement.Abandoned.Count).IsEqualTo(0);
        await Assert.That(client.SessionReceiver.Calls).IsEqualTo(1);
        await Assert.That(client.SessionReceiver.Disposed).IsFalse();
    }

    [Test]
    public async Task Dlq_Rejects_DeadLetter_But_Allows_Complete_Without_Accepting_Session()
    {
        var client = new FakeClient();
        client.Receiver.Receive = (_, _) => Task.FromResult(Messages(1));
        await using var service = ServiceBusFixture.Create(client, new());
        var result = await service.ReceiveLockedMessagesAsync("MyQueue/$DeadLetterQueue", sessionOptions: new(SessionReceiveMode.Specified, ""));
        await using var batch = result.Batch!;
        var handle = batch.Entries[0].Handle;
        await Assert.That((await batch.DeadLetterAsync(handle)).Status).IsEqualTo(MessageSettlementStatus.Unsupported);
        await Assert.That(batch.CanSettle(handle)).IsTrue();
        await Assert.That((await batch.CompleteAsync(handle)).IsSettled).IsTrue();
        await Assert.That(client.SessionCalls.Count).IsEqualTo(0);
        await Assert.That(client.Receiver.DeadLetteredMessages.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Foreign_Handles_And_Double_Parallel_Clicks_Never_Cross_Settle()
    {
        var receiver = new FakeReceiver();
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.Settle = (_, _) => { dispatched.TrySetResult(); return finish.Task; };
        await using var batch = CreateBatch(receiver);
        await using var other = CreateBatch(new());
        await Assert.That((await batch.CompleteAsync(other.Entries[0].Handle)).Status).IsEqualTo(MessageSettlementStatus.InvalidHandle);
        var handle = batch.Entries[0].Handle;
        var first = batch.CompleteAsync(handle);
        await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = batch.DeadLetterAsync(handle);
        finish.SetResult();
        await Assert.That((await first).IsSettled).IsTrue();
        await Assert.That((await second).Status).IsEqualTo(MessageSettlementStatus.NotActionable);
        await Assert.That(receiver.CompletedMessages.Count).IsEqualTo(1);
        await Assert.That(receiver.DeadLetteredMessages.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Expiry_And_Missing_Deadline_Fail_Closed_And_Close_Without_Abandon(bool missing)
    {
        var clock = new FakeClock();
        var receiver = new FakeReceiver();
        await using var batch = CreateBatch(receiver, clock: clock, messages: Messages(2, missing ? DateTimeOffset.MinValue : clock.Now.AddSeconds(2)));
        clock.Now += TimeSpan.FromSeconds(3);
        await Assert.That(batch.CanSettle(batch.Entries[0].Handle)).IsFalse();
        await batch.RefreshExpiryAsync();
        await Assert.That(batch.Entries.All(e => e.State == LockedMessageState.Expired)).IsTrue();
        await Assert.That(receiver.Disposed).IsTrue();
        await Assert.That(receiver.Abandoned.Count).IsEqualTo(0);
        await Assert.That((await batch.CompleteAsync(batch.Entries[0].Handle)).Status).IsEqualTo(MessageSettlementStatus.NotActionable);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Broker_LockLoss_Is_Authoritative(bool session)
    {
        var receiver = new FakeReceiver { Settle = (_, _) => Task.FromException(new ServiceBusException("lost",
            session ? ServiceBusFailureReason.SessionLockLost : ServiceBusFailureReason.MessageLockLost)) };
        await using var batch = CreateBatch(receiver);
        var result = await batch.CompleteAsync(batch.Entries[0].Handle);
        await Assert.That(result.Status).IsEqualTo(MessageSettlementStatus.LockLost);
        await Assert.That(batch.Entries[0].State).IsEqualTo(LockedMessageState.LockLost);
        await Assert.That(batch.Entries[1].State).IsEqualTo(session ? LockedMessageState.LockLost : LockedMessageState.Locked);
        await Assert.That((await batch.CompleteAsync(batch.Entries[0].Handle)).Status).IsEqualTo(MessageSettlementStatus.NotActionable);
        await Assert.That(receiver.CompletedMessages.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Cancellation_Before_Dispatch_Keeps_Lock_Actionable()
    {
        var receiver = new FakeReceiver();
        await using var batch = CreateBatch(receiver);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await batch.CompleteAsync(batch.Entries[0].Handle, cts.Token);
        await Assert.That(result.Status).IsEqualTo(MessageSettlementStatus.Cancelled);
        await Assert.That(batch.CanSettle(batch.Entries[0].Handle)).IsTrue();
        await Assert.That(receiver.CompletedMessages.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Dispatched_Failure_Or_Cancellation_Is_Unknown_And_Never_Retried(bool cancel)
    {
        using var cts = new CancellationTokenSource();
        var receiver = new FakeReceiver { Settle = (_, _) =>
        {
            if (cancel) { cts.Cancel(); return Task.FromCanceled(cts.Token); }
            return Task.FromException(new InvalidOperationException("transport ambiguity"));
        } };
        await using var batch = CreateBatch(receiver);
        var result = await batch.DeadLetterAsync(batch.Entries[0].Handle, cancellationToken: cts.Token);
        await Assert.That(result.Status).IsEqualTo(MessageSettlementStatus.Unknown);
        await Assert.That(batch.Entries[0].State).IsEqualTo(LockedMessageState.Unknown);
        await Assert.That((await batch.DeadLetterAsync(batch.Entries[0].Handle)).Status).IsEqualTo(MessageSettlementStatus.NotActionable);
        await Assert.That(receiver.DeadLetteredMessages.Count).IsEqualTo(1);
        await Assert.That(receiver.Abandoned.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Hanging_Settlement_Has_Bounded_Wait_And_Disposal_Never_Closes_Concurrently()
    {
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = new FakeReceiver
        {
            Settle = (_, _) => { dispatched.SetResult(); return finish.Task; },
            Dispose = () => { closed.TrySetResult(); return ValueTask.CompletedTask; }
        };
        await using var batch = CreateBatch(receiver, settlementTimeout: TimeSpan.FromMilliseconds(40), cleanupTimeout: TimeSpan.FromMilliseconds(40));
        var settling = batch.CompleteAsync(batch.Entries[0].Handle);
        await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var disposing = batch.DisposeAsync().AsTask();
        await Task.WhenAll(settling, disposing).WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That((await settling).Status).IsEqualTo(MessageSettlementStatus.Unknown);
        await Assert.That(receiver.Disposed).IsFalse();
        await Assert.That(batch.CanSettle(batch.Entries[1].Handle)).IsFalse();
        finish.SetException(new InvalidOperationException("late failure must be observed"));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await batch.DisposeAsync();
        await Assert.That(receiver.DisposeCalls).IsEqualTo(1);
        await Assert.That(receiver.Abandoned.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Hanging_Or_Failing_Disposal_Is_Bounded_And_Idempotent()
    {
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = new FakeReceiver { Dispose = () => new ValueTask(finish.Task) };
        await using var batch = CreateBatch(receiver, cleanupTimeout: TimeSpan.FromMilliseconds(40));
        await Task.WhenAll(batch.DisposeAsync().AsTask(), batch.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(receiver.DisposeCalls).IsEqualTo(1);
        await Assert.That(batch.Entries.All(e => e.State == LockedMessageState.Released)).IsTrue();
        await Assert.That((await batch.CompleteAsync(batch.Entries[0].Handle)).Status).IsEqualTo(MessageSettlementStatus.NotActionable);
        finish.SetException(new InvalidOperationException("late dispose failure"));
        await batch.DisposeAsync();
        await Assert.That(receiver.Abandoned.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Empty_Failure_PreCancelled_And_Cancelled_Return_Do_Not_Leak_Ownership()
    {
        var client = new FakeClient();
        await using var service = ServiceBusFixture.Create(client, new());
        var empty = await service.ReceiveLockedMessagesAsync("MyQueue");
        await Assert.That(empty.Batch).IsNull();
        await Assert.That(empty.Status).IsEqualTo(MessageOperationStatus.Completed);
        await Assert.That(client.Receiver.DisposeCalls).IsEqualTo(1);
        client.Receiver.Receive = (_, _) => Task.FromException<IReadOnlyList<ServiceBusReceivedMessage>>(new InvalidOperationException("failed"));
        var failure = await service.ReceiveLockedMessagesAsync("MyQueue");
        await Assert.That(failure.Batch).IsNull();
        await Assert.That(failure.Status).IsEqualTo(MessageOperationStatus.Failed);
        await Assert.That(client.Receiver.DisposeCalls).IsEqualTo(2);
        using var cts = new CancellationTokenSource();
        client.Receiver.Receive = (_, _) => { cts.Cancel(); return Task.FromResult(Messages(1)); };
        var cancelled = await service.ReceiveLockedMessagesAsync("MyQueue", cancellationToken: cts.Token);
        await Assert.That(cancelled.Status).IsEqualTo(MessageOperationStatus.Cancelled);
        await Assert.That(cancelled.Batch).IsNull();
        await Assert.That(client.Receiver.DisposeCalls).IsEqualTo(3);
        var preCancelled = await service.ReceiveLockedMessagesAsync("MyQueue", cancellationToken: cts.Token);
        await Assert.That(preCancelled.Status).IsEqualTo(MessageOperationStatus.Cancelled);
        await Assert.That(client.OrdinaryCalls.Count).IsEqualTo(3);
        await Assert.That(client.Receiver.Abandoned.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Late_Accept_Or_Receive_Is_Observed_Then_Closed_Not_Concurrent(bool accept)
    {
        var client = new FakeClient();
        using var cts = new CancellationTokenSource();
        var accepted = new TaskCompletionSource<ServiceBusSessionReceiver>(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<IReadOnlyList<ServiceBusReceivedMessage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (accept) client.Accept = _ => { cts.Cancel(); return accepted.Task; };
        else client.Receiver.Receive = (_, _) => { cts.Cancel(); return received.Task; };
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (accept) client.SessionReceiver.Dispose = () => { closed.TrySetResult(); return ValueTask.CompletedTask; };
        else client.Receiver.Dispose = () => { closed.TrySetResult(); return ValueTask.CompletedTask; };
        await using var service = ServiceBusFixture.Create(client, new());
        var result = await service.ReceiveLockedMessagesAsync("MyQueue", sessionOptions: accept ? new(SessionReceiveMode.NextAvailable) : null, cancellationToken: cts.Token);
        await Assert.That(result.Status).IsEqualTo(MessageOperationStatus.Cancelled);
        await Assert.That(result.Batch).IsNull();
        await Assert.That(closed.Task.IsCompleted).IsFalse();
        if (accept) accepted.SetResult(client.SessionReceiver); else received.SetResult(Messages(2));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(client.Receiver.Abandoned.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Locked_Client_Is_Separate_From_Legacy_Client_And_Disposed()
    {
        var legacy = new FakeClient();
        var locked = new FakeClient();
        await using var service = new ServiceBusService("localhost", NullLogger<ServiceBusService>.Instance, new FakeEntityRepository(),
            _ => legacy, lockedClientFactory: _ => locked);
        await service.ReceiveLockedMessagesAsync("MyQueue");
        await service.ReceiveMessagesAsync("MyQueue");
        await Assert.That(locked.OrdinaryCalls.Single().Mode).IsEqualTo(ServiceBusReceiveMode.PeekLock);
        await Assert.That(legacy.OrdinaryCalls.Single().Mode).IsEqualTo(ServiceBusReceiveMode.ReceiveAndDelete);
        await service.DisposeAsync();
        await Assert.That(locked.Disposed).IsTrue();
        await Assert.That(legacy.Disposed).IsTrue();
    }

    [Test]
    [Arguments("missing", 20, SessionReceiveMode.Standard, null, MessageOperationStatus.EntityNotFound)]
    [Arguments("MyQueue", 0, SessionReceiveMode.Standard, null, MessageOperationStatus.Failed)]
    [Arguments("MyQueue", 101, SessionReceiveMode.Standard, null, MessageOperationStatus.Failed)]
    [Arguments("MyQueue", 20, SessionReceiveMode.Specified, " ", MessageOperationStatus.InvalidSessionId)]
    public async Task Invalid_Acquisition_Does_Not_Create_Receiver(string entity, int size, SessionReceiveMode mode,
        string? sessionId, MessageOperationStatus expected)
    {
        var client = new FakeClient();
        await using var service = ServiceBusFixture.Create(client, new());
        var result = await service.ReceiveLockedMessagesAsync(entity, size, new(mode, sessionId));
        await Assert.That(result.Status).IsEqualTo(expected);
        await Assert.That(result.Batch).IsNull();
        await Assert.That(client.OrdinaryCalls.Count + client.SessionCalls.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(ServiceBusFailureReason.ServiceTimeout, MessageOperationStatus.SessionUnavailable)]
    [Arguments(ServiceBusFailureReason.SessionCannotBeLocked, MessageOperationStatus.SessionLocked)]
    public async Task Session_Accept_Failures_Have_Safe_Outcomes(ServiceBusFailureReason reason, MessageOperationStatus expected)
    {
        var client = new FakeClient { Accept = _ => Task.FromException<ServiceBusSessionReceiver>(new ServiceBusException("private", reason)) };
        await using var service = ServiceBusFixture.Create(client, new());
        var result = await service.ReceiveLockedMessagesAsync("MyQueue", sessionOptions: new(SessionReceiveMode.NextAvailable));
        await Assert.That(result.Status).IsEqualTo(expected);
        await Assert.That(result.Batch).IsNull();
        await Assert.That(client.SessionCalls.Count).IsEqualTo(1);
        await Assert.That(client.SessionReceiver.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task All_Terminal_Entries_Close_Original_Receiver_Once()
    {
        var receiver = new FakeReceiver();
        await using var batch = CreateBatch(receiver);
        await Assert.That((await batch.CompleteAsync(batch.Entries[0].Handle)).IsSettled).IsTrue();
        await Assert.That(receiver.Disposed).IsFalse();
        await Assert.That((await batch.DeadLetterAsync(batch.Entries[1].Handle)).IsSettled).IsTrue();
        await batch.DisposeAsync();
        await Assert.That(receiver.DisposeCalls).IsEqualTo(1);
        await Assert.That(batch.Entries[0].State).IsEqualTo(LockedMessageState.Completed);
        await Assert.That(batch.Entries[1].State).IsEqualTo(LockedMessageState.DeadLettered);
        await Assert.That(receiver.Abandoned.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Batch_Self_Expires_Without_Viewer_Refresh()
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = new FakeReceiver { Dispose = () => { closed.TrySetResult(); return ValueTask.CompletedTask; } };
        await using var batch = CreateBatch(receiver, messages: Messages(2, DateTimeOffset.UtcNow.AddMilliseconds(50)));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.That(batch.Entries.All(e => e.State == LockedMessageState.Expired)).IsTrue();
        await Assert.That(receiver.DisposeCalls).IsEqualTo(1);
        await Assert.That(receiver.Abandoned.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Session_Expiry_Uses_Session_Deadline_Not_Future_Individual_Lock()
    {
        var client = new FakeClient();
        var clock = new FakeClock();
        client.SessionReceiver.LockDeadline = clock.Now.AddSeconds(2);
        client.SessionReceiver.Receive = (_, _) => Task.FromResult(Messages(2, clock.Now.AddMinutes(2)));
        await using var service = ServiceBusFixture.Create(client, new(), clock);
        var result = await service.ReceiveLockedMessagesAsync("MyQueue", sessionOptions: new(SessionReceiveMode.NextAvailable));
        await using var batch = result.Batch!;
        clock.Now += TimeSpan.FromSeconds(3);
        await batch.RefreshExpiryAsync();
        await Assert.That(batch.Entries.All(e => e.State == LockedMessageState.Expired)).IsTrue();
        await Assert.That(client.SessionReceiver.Disposed).IsTrue();
        await Assert.That(client.SessionReceiver.Settlement.Abandoned.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Acquisition_Cleanup_Failure_Does_Not_Turn_Empty_Result_Into_Leak_Or_Exception()
    {
        var client = new FakeClient();
        client.Receiver.DisposeFailure = new InvalidOperationException("cleanup failure");
        await using var service = ServiceBusFixture.Create(client, new());
        var result = await service.ReceiveLockedMessagesAsync("MyQueue");
        await Assert.That(result.Status).IsEqualTo(MessageOperationStatus.Completed);
        await Assert.That(result.Batch).IsNull();
        await Assert.That(client.Receiver.DisposeCalls).IsEqualTo(1);
    }

    [Test]
    [Arguments("MyQueue/$DeadLetterQueue", "myqueue", false)]
    [Arguments("MyTopic|MySub/$DeadLetterQueue", "mytopic", true)]
    public async Task Replay_Sends_Original_Copy_Before_Completing_Exact_Original(string path, string destination, bool topic)
    {
        var client = new FakeClient();
        var originals = Messages(2);
        client.Receiver.Receive = (_, _) => Task.FromResult(originals);
        var order = new List<string>();
        client.Sender.Send = (_, _) => { order.Add("send"); return Task.CompletedTask; };
        client.Receiver.Settle = (_, _) => { order.Add("complete"); return Task.CompletedTask; };
        await using var service = ServiceBusFixture.Create(client, new());
        var received = await service.ReceiveLockedMessagesAsync(path);
        await using var batch = received.Batch!;
        await Assert.That(client.SenderPaths.Count).IsEqualTo(0);
        await Assert.That(batch.ReplayDestination).IsEqualTo(destination);
        await Assert.That(batch.ReplayToTopic).IsEqualTo(topic);
        var result = await batch.ReplayAsync(batch.Entries[0].Handle, "fresh-id");
        await Assert.That(result.IsSendConfirmed && result.IsCompleted).IsTrue();
        await Assert.That(result.MessageId).IsEqualTo("fresh-id");
        await Assert.That(result.Destination).IsEqualTo(destination);
        await Assert.That(string.Join(",", order)).IsEqualTo("send,complete");
        await Assert.That(client.SenderPaths.Single()).IsEqualTo(destination);
        await Assert.That(ReferenceEquals(client.Receiver.CompletedMessages.Single(), originals[0])).IsTrue();
        await Assert.That(batch.Entries[1].State).IsEqualTo(LockedMessageState.Locked);
        await Assert.That(batch.CanReplay(batch.Entries[0].Handle)).IsFalse();
        await Assert.That(client.Receiver.BatchSizes.Count).IsEqualTo(1);
        await Assert.That(client.SessionCalls.Count + client.Receiver.Abandoned.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Replay_Copy_Accepts_Ordinary_Absent_Optional_Broker_Properties()
    {
        var original = Messages(1).Single();
        var copy = ServiceBusService.CreateReplayMessage(original, "fresh");
        await Assert.That(copy.MessageId).IsEqualTo("fresh");
    }

    [Test]
    public async Task Replay_Preserves_Raw_Binary_Typed_Properties_And_Writable_Broker_Fields_Only()
    {
        var client = new FakeClient();
        var bytes = new byte[] { 0, 255, 128, 13, 10, 1 };
        var original = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromBytes(bytes),
            messageId: "old-id", sessionId: "MiXeD", replyToSessionId: "ReplyCase", correlationId: "correlation",
            contentType: "application/octet-stream", subject: "subject", to: "to", replyTo: "reply", partitionKey: "MiXeD",
            viaPartitionKey: "transaction", timeToLive: TimeSpan.FromMinutes(23), scheduledEnqueueTime: DateTimeOffset.UtcNow.AddHours(1),
            properties: new Dictionary<string, object> { ["number"] = 42, ["flag"] = true, ["binary"] = bytes,
                ["date"] = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), ["custom"] = "kept",
                ["DeadLetterReason"] = "reason", ["DeadLetterErrorDescription"] = "diagnostics" },
            deliveryCount: 7, sequenceNumber: 91, enqueuedSequenceNumber: 17, lockedUntil: DateTimeOffset.UtcNow.AddMinutes(1));
        client.Receiver.Receive = (_, _) => Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>([original]);
        await using var service = ServiceBusFixture.Create(client, new());
        var received = await service.ReceiveLockedMessagesAsync("MyQueue/$DeadLetterQueue");
        await using var batch = received.Batch!;
        var result = await batch.ReplayAsync(batch.Entries[0].Handle, "fresh");
        var copy = client.Sender.Messages.Single();
        await Assert.That(result.IsCompleted).IsTrue();
        await Assert.That(copy.Body.ToArray().SequenceEqual(bytes)).IsTrue();
        await Assert.That(copy.MessageId).IsEqualTo("fresh");
        await Assert.That(copy.SessionId).IsEqualTo("MiXeD");
        await Assert.That(copy.ReplyToSessionId).IsEqualTo("ReplyCase");
        await Assert.That(copy.CorrelationId).IsEqualTo("correlation");
        await Assert.That(copy.ContentType).IsEqualTo("application/octet-stream");
        await Assert.That(copy.Subject).IsEqualTo("subject");
        await Assert.That(copy.To).IsEqualTo("to");
        await Assert.That(copy.ReplyTo).IsEqualTo("reply");
        await Assert.That(copy.PartitionKey).IsEqualTo("MiXeD");
        await Assert.That(copy.TransactionPartitionKey).IsEqualTo("transaction");
        await Assert.That(copy.TimeToLive).IsEqualTo(TimeSpan.FromMinutes(23));
        await Assert.That(copy.ScheduledEnqueueTime).IsEqualTo(default(DateTimeOffset));
        await Assert.That(copy.ApplicationProperties.Count).IsEqualTo(5);
        foreach (var key in new[] { "number", "flag", "binary", "date", "custom" })
        {
            await Assert.That(copy.ApplicationProperties[key]).IsEqualTo(original.ApplicationProperties[key]);
            await Assert.That(copy.ApplicationProperties[key].GetType()).IsEqualTo(original.ApplicationProperties[key].GetType());
        }
        await Assert.That(original.MessageId).IsEqualTo("old-id");
        await Assert.That(original.DeliveryCount).IsEqualTo(7);
        await Assert.That(original.ApplicationProperties.ContainsKey("DeadLetterReason")).IsTrue();
        await Assert.That(ReferenceEquals(client.Receiver.CompletedMessages.Single(), original)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Replay_Send_Failure_Or_Dispatch_Cancellation_Never_Completes_Or_Resends(bool cancel)
    {
        using var cts = new CancellationTokenSource();
        var sender = new FakeSender { Send = (_, _) =>
        {
            if (cancel) { cts.Cancel(); return Task.FromCanceled(cts.Token); }
            return Task.FromException(new InvalidOperationException("ambiguous transport"));
        } };
        var receiver = new FakeReceiver();
        await using var batch = CreateReplayBatch(receiver, sender);
        var handle = batch.Entries[0].Handle;
        var result = await batch.ReplayAsync(handle, "fresh", cts.Token);
        await Assert.That(result.SendStatus).IsEqualTo(MessageReplaySendStatus.Unknown);
        await Assert.That(result.CompletionStatus).IsNull();
        await Assert.That(batch.Entries[0].State).IsEqualTo(LockedMessageState.Unknown);
        await Assert.That(batch.CanReplay(handle)).IsFalse();
        await Assert.That((await batch.ReplayAsync(handle, "another")).SendStatus).IsEqualTo(MessageReplaySendStatus.NotActionable);
        await Assert.That((await batch.CompleteAsync(handle)).IsSettled).IsFalse();
        await Assert.That(receiver.CompletedMessages.Count).IsEqualTo(0);
        await Assert.That(sender.Messages.Count).IsEqualTo(1);
        await Assert.That(batch.Entries[1].State).IsEqualTo(LockedMessageState.Locked);
    }

    [Test]
    [Arguments(ServiceBusFailureReason.MessageLockLost)]
    [Arguments(ServiceBusFailureReason.SessionLockLost)]
    [Arguments(ServiceBusFailureReason.ServiceCommunicationProblem)]
    public async Task Replay_Confirmed_Send_With_Completion_Failure_Retains_Progress_And_No_Resend(ServiceBusFailureReason reason)
    {
        var receiver = new FakeReceiver { Settle = (_, _) => Task.FromException(new ServiceBusException("failed", reason)) };
        var sender = new FakeSender();
        await using var batch = CreateReplayBatch(receiver, sender);
        var result = await batch.ReplayAsync(batch.Entries[0].Handle, "fresh");
        await Assert.That(result.IsSendConfirmed).IsTrue();
        await Assert.That(result.IsCompleted).IsFalse();
        await Assert.That(result.CompletionStatus).IsEqualTo(reason == ServiceBusFailureReason.ServiceCommunicationProblem
            ? MessageSettlementStatus.Unknown : MessageSettlementStatus.LockLost);
        await Assert.That(batch.Entries[1].State).IsEqualTo(reason == ServiceBusFailureReason.SessionLockLost
            ? LockedMessageState.LockLost : LockedMessageState.Locked);
        await Assert.That((await batch.ReplayAsync(batch.Entries[0].Handle, "another")).IsSendConfirmed).IsFalse();
        await Assert.That(sender.Messages.Count).IsEqualTo(1);
        await Assert.That(receiver.CompletedMessages.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Replay_Expiry_Or_Cancellation_After_Confirmed_Send_Does_Not_Complete(bool cancel)
    {
        var clock = new FakeClock();
        using var cts = new CancellationTokenSource();
        var sender = new FakeSender { Send = (_, _) =>
        {
            if (cancel) cts.Cancel(); else clock.Now += TimeSpan.FromMinutes(2);
            return Task.CompletedTask;
        } };
        var receiver = new FakeReceiver();
        await using var batch = CreateReplayBatch(receiver, sender, clock);
        var result = await batch.ReplayAsync(batch.Entries[0].Handle, "fresh", cts.Token);
        await Assert.That(result.IsSendConfirmed).IsTrue();
        await Assert.That(result.IsCompleted).IsFalse();
        await Assert.That(result.CompletionStatus).IsEqualTo(cancel ? MessageSettlementStatus.Cancelled : MessageSettlementStatus.NotActionable);
        await Assert.That(receiver.CompletedMessages.Count).IsEqualTo(0);
        await Assert.That(batch.CanReplay(batch.Entries[0].Handle)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Replay_Late_Send_Or_Completion_Retains_Gate_Until_Task_Ends(bool completion)
    {
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var senderClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = new FakeReceiver
        {
            Dispose = () => { closed.TrySetResult(); return ValueTask.CompletedTask; }
        };
        var sender = new FakeSender { Dispose = () => { senderClosed.TrySetResult(); return ValueTask.CompletedTask; } };
        if (completion) receiver.Settle = (_, _) => { dispatched.SetResult(); return finish.Task; };
        else sender.Send = (_, _) => { dispatched.SetResult(); return finish.Task; };
        await using var batch = CreateReplayBatch(receiver, sender, settlementTimeout: TimeSpan.FromMilliseconds(40), cleanupTimeout: TimeSpan.FromMilliseconds(40));
        var replaying = batch.ReplayAsync(batch.Entries[0].Handle, "fresh");
        await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var disposing = batch.DisposeAsync().AsTask();
        await Task.WhenAll(replaying, disposing).WaitAsync(TimeSpan.FromSeconds(2));
        var result = await replaying;
        await Assert.That(result.IsSendConfirmed).IsEqualTo(completion);
        await Assert.That(result.IsCompleted).IsFalse();
        await Assert.That(batch.Entries[0].State).IsEqualTo(LockedMessageState.Unknown);
        await Assert.That(closed.Task.IsCompleted || senderClosed.Task.IsCompleted).IsFalse();
        finish.SetResult(); // Late success remains ambiguous to the caller and does not trigger completion/resend.
        await Task.WhenAll(closed.Task, senderClosed.Task).WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(receiver.CompletedMessages.Count).IsEqualTo(completion ? 1 : 0);
        await Assert.That(sender.Messages.Count).IsEqualTo(1);
        await Assert.That(sender.DisposeCalls).IsEqualTo(1);
        await Assert.That(receiver.Abandoned.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Replay_Concurrent_Complete_Double_Click_And_Disposal_Are_Serialized()
    {
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = new FakeSender { Send = (_, _) => { dispatched.SetResult(); return finish.Task; } };
        var receiver = new FakeReceiver();
        await using var batch = CreateReplayBatch(receiver, sender);
        var handle = batch.Entries[0].Handle;
        var replay = batch.ReplayAsync(handle, "fresh");
        await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var complete = batch.CompleteAsync(handle);
        var repeat = batch.ReplayAsync(handle, "repeat");
        finish.SetResult();
        await Assert.That((await replay).IsCompleted).IsTrue();
        await Assert.That((await complete).Status).IsEqualTo(MessageSettlementStatus.NotActionable);
        await Assert.That((await repeat).SendStatus).IsEqualTo(MessageReplaySendStatus.NotActionable);
        await batch.DisposeAsync();
        await Assert.That(sender.Messages.Count).IsEqualTo(1);
        await Assert.That(receiver.CompletedMessages.Count).IsEqualTo(1);
        await Assert.That(receiver.DisposeCalls).IsEqualTo(1);
    }

    [Test]
    public async Task Replay_Predispatch_Rejections_Never_Create_Sender_Or_Change_Actionable_Lock()
    {
        var client = new FakeClient();
        client.Receiver.Receive = (_, _) => Task.FromResult(Messages(2));
        await using var service = ServiceBusFixture.Create(client, new());
        var received = await service.ReceiveLockedMessagesAsync("MyQueue/$DeadLetterQueue");
        await using var batch = received.Batch!;
        await using var foreign = CreateBatch(new());
        var handle = batch.Entries[0].Handle;
        await Assert.That((await batch.ReplayAsync(foreign.Entries[0].Handle, "fresh")).SendStatus).IsEqualTo(MessageReplaySendStatus.InvalidHandle);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.That((await batch.ReplayAsync(handle, "fresh", cts.Token)).SendStatus).IsEqualTo(MessageReplaySendStatus.Cancelled);
        foreach (var id in new[] { "", " ", "m-0", new string('x', 129) })
            await Assert.That((await batch.ReplayAsync(handle, id)).SendStatus).IsEqualTo(MessageReplaySendStatus.NotSent);
        await Assert.That(client.SenderPaths.Count).IsEqualTo(0);
        await Assert.That(batch.CanReplay(handle)).IsTrue();
        client.SenderCreationFailure = new InvalidOperationException("create failure");
        await Assert.That((await batch.ReplayAsync(handle, "fresh")).SendStatus).IsEqualTo(MessageReplaySendStatus.NotSent);
        await Assert.That(batch.CanReplay(handle)).IsTrue();
        await Assert.That(client.Sender.Messages.Count + client.Receiver.CompletedMessages.Count).IsEqualTo(0);
        await batch.DisposeAsync();
        await Assert.That((await batch.ReplayAsync(handle, "fresh")).SendStatus).IsEqualTo(MessageReplaySendStatus.NotActionable);
    }

    [Test]
    public async Task Replay_Main_Unknown_Route_And_Expired_Handle_Fail_Closed()
    {
        var receiver = new FakeReceiver();
        await using var main = CreateBatch(receiver);
        await Assert.That((await main.ReplayAsync(main.Entries[0].Handle, "fresh")).SendStatus).IsEqualTo(MessageReplaySendStatus.Unsupported);
        await Assert.That(main.ReplayDestination).IsNull();
        await using var unknown = new LockedMessageBatch(new FakeReceiver(), Messages(2), _ => new(), true,
            TimeProvider.System, NullLogger.Instance);
        await Assert.That((await unknown.ReplayAsync(unknown.Entries[0].Handle, "fresh")).SendStatus).IsEqualTo(MessageReplaySendStatus.Unsupported);
        var clock = new FakeClock();
        var sender = new FakeSender();
        await using var expired = CreateReplayBatch(new(), sender, clock);
        clock.Now += TimeSpan.FromMinutes(2);
        await Assert.That((await expired.ReplayAsync(expired.Entries[0].Handle, "fresh")).SendStatus).IsEqualTo(MessageReplaySendStatus.NotActionable);
        await Assert.That(sender.Messages.Count).IsEqualTo(0);
        await Assert.That(receiver.CompletedMessages.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Replay_Sender_Cleanup_Failure_Cannot_Undo_Confirmed_Send_Or_Completion()
    {
        var sender = new FakeSender { Dispose = () => ValueTask.FromException(new InvalidOperationException("cleanup")) };
        var receiver = new FakeReceiver { DisposeFailure = new InvalidOperationException("receiver cleanup") };
        await using var batch = CreateReplayBatch(receiver, sender);
        var result = await batch.ReplayAsync(batch.Entries[0].Handle, "fresh");
        await batch.DisposeAsync();
        await Assert.That(result.IsCompleted).IsTrue();
        await Assert.That(result.IsSendConfirmed).IsTrue();
        await Assert.That(sender.DisposeCalls).IsEqualTo(1);
        await Assert.That(receiver.DisposeCalls).IsEqualTo(1);
        await Assert.That(batch.Entries[0].State).IsEqualTo(LockedMessageState.Completed);
    }

    [Test]
    [Arguments("MyQueue", null, false)]
    [Arguments("MyQueue/$DeadLetterQueue", "myqueue", false)]
    [Arguments("MyTopic|MySub/$DeadLetterQueue", "mytopic", true)]
    [Arguments("/$DeadLetterQueue", null, false)]
    [Arguments("topic/subscriptions//$DeadLetterQueue", null, false)]
    [Arguments("topic/subscriptions/sub/subscriptions/other/$DeadLetterQueue", null, false)]
    [Arguments("queue/$DeadLetterQueue/$DeadLetterQueue", null, false)]
    public async Task Replay_Route_Validation_Is_Fail_Closed(string path, string? expected, bool topic)
    {
        var route = ServiceBusService.GetReplayDestination(path);
        await Assert.That(route.Destination).IsEqualTo(expected);
        await Assert.That(route.ToTopic).IsEqualTo(topic);
    }

    [Test]
    public async Task Replay_Uses_Separate_NoRetry_Locked_Client_And_Batches_Are_Isolated()
    {
        var legacy = new FakeClient();
        var locked = new FakeClient();
        locked.Receiver.Receive = (_, _) => Task.FromResult(Messages(2));
        await using var service = new ServiceBusService("localhost", NullLogger<ServiceBusService>.Instance, new FakeEntityRepository(),
            _ => legacy, lockedClientFactory: _ => locked);
        var result = await service.ReceiveLockedMessagesAsync("MyQueue/$DeadLetterQueue");
        await using var batch = result.Batch!;
        var otherSender = new FakeSender();
        var otherReceiver = new FakeReceiver();
        await using var other = CreateReplayBatch(otherReceiver, otherSender);
        await Assert.That((await batch.ReplayAsync(batch.Entries[0].Handle, "fresh")).IsCompleted).IsTrue();
        await Assert.That(legacy.SenderPaths.Count + legacy.Sender.Messages.Count).IsEqualTo(0);
        await Assert.That(locked.Sender.Messages.Count).IsEqualTo(1);
        await Assert.That(otherSender.Messages.Count + otherReceiver.CompletedMessages.Count).IsEqualTo(0);
        await Assert.That(other.Entries.All(e => e.State == LockedMessageState.Locked)).IsTrue();
    }

    [Test]
    public async Task Replay_Late_Send_Failure_Is_Observed_Without_Concurrent_Cleanup()
    {
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = new FakeSender { Send = (_, _) => finish.Task,
            Dispose = () => { closed.TrySetResult(); return ValueTask.CompletedTask; } };
        var receiver = new FakeReceiver();
        await using var batch = CreateReplayBatch(receiver, sender, settlementTimeout: TimeSpan.FromMilliseconds(40), cleanupTimeout: TimeSpan.FromMilliseconds(40));
        var result = await batch.ReplayAsync(batch.Entries[0].Handle, "fresh");
        await batch.DisposeAsync();
        await Assert.That(result.SendStatus).IsEqualTo(MessageReplaySendStatus.Unknown);
        await Assert.That(receiver.Disposed).IsFalse();
        await Assert.That(sender.DisposeCalls).IsEqualTo(0);
        finish.SetException(new InvalidOperationException("late failure"));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(receiver.CompletedMessages.Count).IsEqualTo(0);
        await Assert.That(sender.Messages.Count).IsEqualTo(1);
        await Assert.That(batch.CanReplay(batch.Entries[0].Handle)).IsFalse();
    }

    [Test]
    public async Task Replay_Sender_Disposal_Timeout_Is_Bounded_And_Preserves_Result()
    {
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = new FakeSender { Dispose = () => new ValueTask(finish.Task) };
        var receiver = new FakeReceiver();
        await using var batch = CreateReplayBatch(receiver, sender, cleanupTimeout: TimeSpan.FromMilliseconds(40));
        var result = await batch.ReplayAsync(batch.Entries[0].Handle, "fresh");
        await Task.WhenAll(batch.DisposeAsync().AsTask(), batch.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(result.IsCompleted).IsTrue();
        await Assert.That(sender.DisposeCalls).IsEqualTo(1);
        await Assert.That(receiver.DisposeCalls).IsEqualTo(1);
        finish.SetResult();
        await batch.DisposeAsync();
    }

    [Test]
    public async Task Complete_Winning_Gate_Prevents_Subsequent_Replay()
    {
        var sender = new FakeSender();
        var receiver = new FakeReceiver();
        await using var batch = CreateReplayBatch(receiver, sender);
        var handle = batch.Entries[0].Handle;
        await Assert.That((await batch.CompleteAsync(handle)).IsSettled).IsTrue();
        await Assert.That((await batch.ReplayAsync(handle, "fresh")).SendStatus).IsEqualTo(MessageReplaySendStatus.NotActionable);
        await Assert.That(sender.Messages.Count).IsEqualTo(0);
        await Assert.That(receiver.CompletedMessages.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Replay_Rechecks_Expiry_Or_Cancellation_After_Sender_Creation(bool cancel)
    {
        var clock = new FakeClock();
        using var cts = new CancellationTokenSource();
        var receiver = new FakeReceiver();
        var sender = new FakeSender();
        await using var batch = new LockedMessageBatch(receiver, Messages(2, clock.Now.AddMinutes(1)), _ => new(), true,
            clock, NullLogger.Instance, replayDestination: "myqueue", senderFactory: () =>
            {
                if (cancel) cts.Cancel(); else clock.Now += TimeSpan.FromMinutes(2);
                return sender;
            });
        var result = await batch.ReplayAsync(batch.Entries[0].Handle, "fresh", cts.Token);
        await Assert.That(result.SendStatus).IsEqualTo(cancel ? MessageReplaySendStatus.Cancelled : MessageReplaySendStatus.NotActionable);
        await Assert.That(sender.Messages.Count + receiver.CompletedMessages.Count).IsEqualTo(0);
        await Assert.That(batch.CanReplay(batch.Entries[0].Handle)).IsEqualTo(cancel);
    }

    private static LockedMessageBatch CreateReplayBatch(FakeReceiver receiver, FakeSender sender, FakeClock? clock = null,
        TimeSpan? settlementTimeout = null, TimeSpan? cleanupTimeout = null)
        => new(receiver, Messages(2, clock?.Now.AddMinutes(1)), m => new DisplayedMessage { SequenceNumber = m.SequenceNumber }, true,
            (TimeProvider?)clock ?? TimeProvider.System, NullLogger.Instance, settlementTimeout, cleanupTimeout,
            "myqueue", false, () => sender);

    private static LockedMessageBatch CreateBatch(FakeReceiver receiver, FakeClock? clock = null,
        IReadOnlyList<ServiceBusReceivedMessage>? messages = null, TimeSpan? settlementTimeout = null, TimeSpan? cleanupTimeout = null)
        => new(receiver, messages ?? Messages(2), m => new DisplayedMessage { SequenceNumber = m.SequenceNumber }, false,
            (TimeProvider?)clock ?? TimeProvider.System, NullLogger.Instance, settlementTimeout, cleanupTimeout);

    internal static IReadOnlyList<ServiceBusReceivedMessage> Messages(int count, DateTimeOffset? until = null)
        => Enumerable.Range(0, count).Select(i => ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString("body"), messageId: $"m-{i}", sequenceNumber: i + 1,
            timeToLive: TimeSpan.FromHours(1), sessionId: "CaseSensitiveID", lockedUntil: until ?? DateTimeOffset.UtcNow.AddMinutes(1))).ToList();
}

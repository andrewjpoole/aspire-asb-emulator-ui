using AspireAsbEmulatorUi.App.Models;
using Azure.Messaging.ServiceBus;

namespace AspireAsbEmulatorUi.App.Tests;

public class ServiceBusServiceTests
{
    [Test]
    public async Task CleanEntityName_Preserves_Dots_And_Strips_Prefixes()
    {
        var input = "SBEMULATORNS:TOPIC:FIM1437.PAYMENTS.CORRESPONDENTBANK.VIRTUALACCOUNTNEWVERSIONEVENT";
        var cleaned = Services.ServiceBusService.CleanEntityName(input);

        await Assert.That(cleaned).IsEqualTo("fim1437.payments.correspondentbank.virtualaccountnewversionevent");
    }

    [Test]
    public async Task CleanEntityName_ForSubscription_Format()
    {
        var input = "SBEMULATORNS:TOPIC:MyTopic|MySub";
        var cleaned = Services.ServiceBusService.CleanEntityName(input);
        await Assert.That(cleaned).IsEqualTo("mytopic/subscriptions/mysub");
    }

    [Test]
    [Arguments("SBEMULATORNS:QUEUE:MyQueue", "myqueue")]
    [Arguments("SBEMULATORNS:QUEUE:MyQueue/$DeadLetterQueue", "myqueue/$deadletterqueue")]
    [Arguments("SBEMULATORNS:TOPIC:MyTopic|MySub", "mytopic/subscriptions/mysub")]
    [Arguments("SBEMULATORNS:TOPIC:MyTopic|MySub/$DeadLetterQueue", "mytopic/subscriptions/mysub/$DeadLetterQueue")]
    public async Task Drain_Continues_After_Short_Batches_And_Routes_Selected_Target(string input, string path)
    {
        var client = new FakeClient();
        var repo = new FakeEntityRepository();
        var batches = new Queue<int>([2, 3, 0]);
        client.Receiver.Receive = (_, _) => Task.FromResult(ServiceBusFixture.Messages(batches.Dequeue()));
        await using var svc = ServiceBusFixture.Create(client, repo);
        var result = await svc.DrainMessagesAsync(input);
        await Assert.That(result.DeletedCount).IsEqualTo(5);
        await Assert.That(result.StopReason).IsEqualTo(DrainStopReason.Empty);
        await Assert.That(client.OrdinaryCalls.Single().Path).IsEqualTo(path);
        await Assert.That(client.OrdinaryCalls.Single().Mode).IsEqualTo(ServiceBusReceiveMode.ReceiveAndDelete);
        await Assert.That(client.Receiver.BatchSizes.SequenceEqual([100, 100, 100])).IsTrue();
        await Assert.That(client.Receiver.Waits.All(w => w == TimeSpan.FromSeconds(1))).IsTrue();
        await Assert.That(repo.Calls).IsEqualTo(1);
        await Assert.That(client.Receiver.Disposed).IsTrue();
    }

    [Test]
    public async Task Drain_Unknown_Entity_Does_Not_Create_Receiver()
    {
        var client = new FakeClient();
        await using var svc = ServiceBusFixture.Create(client, new());
        var result = await svc.DrainMessagesAsync("missing");
        await Assert.That(result.StopReason).IsEqualTo(DrainStopReason.EntityNotFound);
        await Assert.That(client.OrdinaryCalls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Drain_Stops_At_Message_Limit()
    {
        var client = new FakeClient();
        client.Receiver.Receive = (size, _) => Task.FromResult(ServiceBusFixture.Messages(size));
        await using var svc = ServiceBusFixture.Create(client, new());
        var result = await svc.DrainMessagesAsync("MyQueue");
        await Assert.That(result.DeletedCount).IsEqualTo(10000);
        await Assert.That(result.StopReason).IsEqualTo(DrainStopReason.MessageLimit);
        await Assert.That(client.Receiver.BatchSizes.Count).IsEqualTo(100);
        await Assert.That(client.Receiver.Disposed).IsTrue();
    }

    [Test]
    public async Task Drain_Stops_At_Time_Limit_Keeping_Progress()
    {
        var client = new FakeClient();
        var clock = new FakeClock();
        client.Receiver.Receive = (_, _) =>
        {
            clock.Now += TimeSpan.FromSeconds(30);
            return Task.FromResult(ServiceBusFixture.Messages(4));
        };
        await using var svc = ServiceBusFixture.Create(client, new(), clock);
        var result = await svc.DrainMessagesAsync("MyQueue");
        await Assert.That(result.DeletedCount).IsEqualTo(4);
        await Assert.That(result.StopReason).IsEqualTo(DrainStopReason.TimeLimit);
        await Assert.That(client.Receiver.BatchSizes.Count).IsEqualTo(1);
        await Assert.That(client.Receiver.Disposed).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Drain_Retains_Progress_On_Failure_Or_Cancellation(bool cancel)
    {
        var client = new FakeClient();
        using var cts = new CancellationTokenSource();
        var calls = 0;
        client.Receiver.Receive = (_, token) =>
        {
            if (calls++ == 0) return Task.FromResult(ServiceBusFixture.Messages(7));
            if (cancel) { cts.Cancel(); token.ThrowIfCancellationRequested(); }
            throw new InvalidOperationException("private transport details");
        };
        await using var svc = ServiceBusFixture.Create(client, new());
        var result = await svc.DrainMessagesAsync("MyQueue", cancellationToken: cts.Token);
        await Assert.That(result.DeletedCount).IsEqualTo(7);
        await Assert.That(result.StopReason).IsEqualTo(cancel ? DrainStopReason.Cancelled : DrainStopReason.Failed);
        await Assert.That(client.Receiver.BatchSizes.Count).IsEqualTo(2);
        await Assert.That(client.Receiver.Disposed).IsTrue();
    }

    [Test]
    public async Task Available_Drain_Honors_Remaining_Message_Budget()
    {
        var client = new FakeClient();
        client.Receiver.Receive = (max, _) => Task.FromResult(ServiceBusFixture.Messages(max));
        await using var svc = ServiceBusFixture.Create(client, new());
        var result = await svc.DrainAvailableMessagesAsync("MyQueue/$DeadLetterQueue", 237);
        await Assert.That(result).IsEqualTo(new DrainMessagesResult(237, DrainStopReason.MessageLimit));
        await Assert.That(client.Receiver.BatchSizes.SequenceEqual(new[] { 100, 100, 37 })).IsTrue();
        await Assert.That(client.SessionCalls.Count).IsEqualTo(0);
        await Assert.That(client.OrdinaryCalls.Single().Mode).IsEqualTo(ServiceBusReceiveMode.ReceiveAndDelete);
    }

    [Test]
    public async Task Available_Drain_Cannot_Exceed_Ordinary_Global_Budget()
    {
        var client = new FakeClient();
        client.Receiver.Receive = (max, _) => Task.FromResult(ServiceBusFixture.Messages(max));
        await using var svc = ServiceBusFixture.Create(client, new());
        var result = await svc.DrainAvailableMessagesAsync("MyQueue/$DeadLetterQueue", 20_000);
        await Assert.That(result).IsEqualTo(new DrainMessagesResult(10_000, DrainStopReason.MessageLimit));
        await Assert.That(client.Receiver.BatchSizes.Sum()).IsEqualTo(10_000);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task Available_Drain_Exhausted_Budget_Makes_No_Broker_Call(int budget)
    {
        var client = new FakeClient();
        var repo = new FakeEntityRepository();
        await using var svc = ServiceBusFixture.Create(client, repo);
        var result = await svc.DrainAvailableMessagesAsync("MyQueue/$DeadLetterQueue", budget);
        await Assert.That(result).IsEqualTo(new DrainMessagesResult(0, DrainStopReason.MessageLimit));
        await Assert.That(client.OrdinaryCalls.Count).IsEqualTo(0);
        await Assert.That(repo.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Drain_Empty_And_PreCancelled_Targets()
    {
        var client = new FakeClient();
        await using var svc = ServiceBusFixture.Create(client, new());
        var empty = await svc.DrainMessagesAsync("MyQueue");
        await Assert.That(empty.DeletedCount).IsEqualTo(0);
        await Assert.That(empty.StopReason).IsEqualTo(DrainStopReason.Empty);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = await svc.DrainMessagesAsync("MyQueue", cancellationToken: cts.Token);
        await Assert.That(cancelled.StopReason).IsEqualTo(DrainStopReason.Cancelled);
        await Assert.That(client.OrdinaryCalls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Ordinary_Receive_And_Peek_Preserve_Mapping_And_Dispose()
    {
        var client = new FakeClient();
        client.Receiver.Receive = (_, _) => Task.FromResult(ServiceBusFixture.Messages(1));
        await using var svc = ServiceBusFixture.Create(client, new());
        var messages = await svc.ReceiveMessagesAsync("MyQueue", 3);
        await Assert.That(messages.Single().SessionId).IsEqualTo("CaseSensitiveID");
        await Assert.That(messages.Single().Body).IsEqualTo("body");
        await Assert.That(client.Receiver.Disposed).IsTrue();
        var peeked = await svc.PeekMessagesAsync("MyQueue", 3);
        await Assert.That(peeked.Count).IsEqualTo(1);
        await Assert.That(client.OrdinaryCalls.Last().Mode).IsEqualTo(ServiceBusReceiveMode.PeekLock);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task Session_Receive_Uses_Correct_Overload_And_Preserves_ID(bool subscription, bool specified)
    {
        var client = new FakeClient();
        await using var svc = ServiceBusFixture.Create(client, new());
        var options = new SessionReceiveOptions(specified ? SessionReceiveMode.Specified : SessionReceiveMode.NextAvailable, "CaseSensitiveID");
        var result = await svc.ReceiveMessagesWithSessionAsync(subscription ? "SBEMULATORNS:TOPIC:MyTopic|MySub" : "SBEMULATORNS:QUEUE:MyQueue", 8, options);
        var call = client.SessionCalls.Single();
        await Assert.That(call.QueueOrTopic).IsEqualTo(subscription ? "mytopic" : "myqueue");
        await Assert.That(call.Subscription).IsEqualTo(subscription ? "mysub" : null);
        await Assert.That(call.Id).IsEqualTo(specified ? "CaseSensitiveID" : null);
        await Assert.That(call.Mode).IsEqualTo(ServiceBusReceiveMode.ReceiveAndDelete);
        await Assert.That(call.Token.CanBeCanceled).IsTrue();
        await Assert.That(result.Status).IsEqualTo(MessageOperationStatus.Completed);
        await Assert.That(result.AcceptedSessionId).IsEqualTo("CaseSensitiveID");
        await Assert.That(result.Messages.Single().SessionId).IsEqualTo("CaseSensitiveID");
        await Assert.That(client.SessionReceiver.Calls).IsEqualTo(1);
        await Assert.That(client.SessionReceiver.Disposed).IsTrue();
        await Assert.That(client.OrdinaryCalls.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Session_Drain_Accepts_Only_One_Session(bool subscription)
    {
        var client = new FakeClient();
        var batches = new Queue<int>([3, 2, 0]);
        client.SessionReceiver.Receive = (_, _) => Task.FromResult(ServiceBusFixture.Messages(batches.Dequeue()));
        await using var svc = ServiceBusFixture.Create(client, new());
        var result = await svc.DrainMessagesAsync(subscription ? "MyTopic|MySub" : "MyQueue", new(SessionReceiveMode.NextAvailable));
        await Assert.That(result.DeletedCount).IsEqualTo(5);
        await Assert.That(result.StopReason).IsEqualTo(DrainStopReason.Empty);
        await Assert.That(result.AcceptedSessionId).IsEqualTo("CaseSensitiveID");
        await Assert.That(client.SessionCalls.Count).IsEqualTo(1);
        await Assert.That(client.SessionReceiver.Disposed).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Dlq_Forces_Ordinary_Mode_Even_With_Invalid_Session_ID(bool subscription)
    {
        var client = new FakeClient();
        await using var svc = ServiceBusFixture.Create(client, new());
        var path = subscription ? "MyTopic|MySub/$DeadLetterQueue" : "MyQueue/$DeadLetterQueue";
        var invalid = new SessionReceiveOptions(SessionReceiveMode.Specified, " ");
        var received = await svc.ReceiveMessagesWithSessionAsync(path, sessionOptions: invalid);
        var drained = await svc.DrainMessagesAsync(path, invalid);
        await Assert.That(received.Status).IsEqualTo(MessageOperationStatus.Completed);
        await Assert.That(drained.StopReason).IsEqualTo(DrainStopReason.Empty);
        await Assert.That(client.SessionCalls.Count).IsEqualTo(0);
        await Assert.That(client.OrdinaryCalls.Count).IsEqualTo(2);
        await Assert.That(client.Receiver.Disposed).IsTrue();
    }

    [Test]
    [Arguments(ServiceBusFailureReason.ServiceTimeout, SessionReceiveMode.NextAvailable, MessageOperationStatus.SessionUnavailable, DrainStopReason.SessionUnavailable)]
    [Arguments(ServiceBusFailureReason.ServiceTimeout, SessionReceiveMode.Specified, MessageOperationStatus.SessionUnavailable, DrainStopReason.SessionUnavailable)]
    [Arguments(ServiceBusFailureReason.SessionCannotBeLocked, SessionReceiveMode.Specified, MessageOperationStatus.SessionLocked, DrainStopReason.SessionLocked)]
    [Arguments(ServiceBusFailureReason.GeneralError, SessionReceiveMode.NextAvailable, MessageOperationStatus.Failed, DrainStopReason.Failed)]
    public async Task Session_Accept_Failure_Is_Safe_And_Does_Not_Fallback(ServiceBusFailureReason reason, SessionReceiveMode mode, MessageOperationStatus status, DrainStopReason stopReason)
    {
        var client = new FakeClient { Accept = _ => throw new ServiceBusException("secret error", reason) };
        await using var svc = ServiceBusFixture.Create(client, new());
        var options = new SessionReceiveOptions(mode, "CaseSensitiveID");
        var received = await svc.ReceiveMessagesWithSessionAsync("MyQueue", sessionOptions: options);
        var drained = await svc.DrainMessagesAsync("MyQueue", options);
        await Assert.That(received.Status).IsEqualTo(status);
        await Assert.That(drained.StopReason).IsEqualTo(stopReason);
        await Assert.That(client.OrdinaryCalls.Count).IsEqualTo(0);
        await Assert.That(client.SessionCalls.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Invalid_Session_ID_And_Unknown_Entity_Do_Not_Accept()
    {
        var client = new FakeClient();
        await using var svc = ServiceBusFixture.Create(client, new());
        var options = new SessionReceiveOptions(SessionReceiveMode.Specified, " ");
        var received = await svc.ReceiveMessagesWithSessionAsync("MyQueue", sessionOptions: options);
        var drained = await svc.DrainMessagesAsync("MyQueue", options);
        var missing = await svc.ReceiveMessagesWithSessionAsync("missing", sessionOptions: new(SessionReceiveMode.NextAvailable));
        await Assert.That(received.Status).IsEqualTo(MessageOperationStatus.InvalidSessionId);
        await Assert.That(drained.StopReason).IsEqualTo(DrainStopReason.InvalidSessionId);
        await Assert.That(missing.Status).IsEqualTo(MessageOperationStatus.EntityNotFound);
        await Assert.That(client.SessionCalls.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Session_Drain_Error_Or_Cancellation_Keeps_Count_And_ID(bool cancel)
    {
        var client = new FakeClient();
        using var cts = new CancellationTokenSource();
        client.SessionReceiver.Receive = (_, token) =>
        {
            if (client.SessionReceiver.Calls == 1) return Task.FromResult(ServiceBusFixture.Messages(2));
            if (cancel) { cts.Cancel(); token.ThrowIfCancellationRequested(); }
            throw new InvalidOperationException("private error");
        };
        await using var svc = ServiceBusFixture.Create(client, new());
        var result = await svc.DrainMessagesAsync("MyQueue", new(SessionReceiveMode.Specified, "CaseSensitiveID"), cts.Token);
        await Assert.That(result.DeletedCount).IsEqualTo(2);
        await Assert.That(result.StopReason).IsEqualTo(cancel ? DrainStopReason.Cancelled : DrainStopReason.Failed);
        await Assert.That(result.AcceptedSessionId).IsEqualTo("CaseSensitiveID");
        await Assert.That(client.SessionReceiver.Disposed).IsTrue();
    }

    [Test]
    public async Task Session_Accept_Cancellation_Does_Not_Fallback()
    {
        using var cts = new CancellationTokenSource();
        var client = new FakeClient { Accept = token => { cts.Cancel(); token.ThrowIfCancellationRequested(); throw new Exception(); } };
        await using var svc = ServiceBusFixture.Create(client, new());
        var result = await svc.ReceiveMessagesWithSessionAsync("MyQueue", sessionOptions: new(SessionReceiveMode.NextAvailable), cancellationToken: cts.Token);
        await Assert.That(result.Status).IsEqualTo(MessageOperationStatus.Cancelled);
        await Assert.That(client.OrdinaryCalls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Session_Accept_Has_Five_Second_Deadline_For_Receive_And_Drain()
    {
        var client = new FakeClient
        {
            Accept = async token => { await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new Exception(); }
        };
        await using var svc = ServiceBusFixture.Create(client, new());
        var receive = svc.ReceiveMessagesWithSessionAsync("MyQueue", sessionOptions: new(SessionReceiveMode.NextAvailable));
        var drain = svc.DrainMessagesAsync("MyQueue", new(SessionReceiveMode.NextAvailable));
        var received = await receive.WaitAsync(TimeSpan.FromSeconds(10));
        var drained = await drain.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(received.Status).IsEqualTo(MessageOperationStatus.SessionUnavailable);
        await Assert.That(drained.StopReason).IsEqualTo(DrainStopReason.SessionUnavailable);
        await Assert.That(client.OrdinaryCalls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Drain_Disposal_Error_Does_Not_Lose_Progress()
    {
        var client = new FakeClient();
        var batches = new Queue<int>([4, 0]);
        client.Receiver.Receive = (_, _) => Task.FromResult(ServiceBusFixture.Messages(batches.Dequeue()));
        client.Receiver.DisposeFailure = new InvalidOperationException("private disposal error");
        await using var svc = ServiceBusFixture.Create(client, new());
        var result = await svc.DrainMessagesAsync("MyQueue");
        await Assert.That(result.DeletedCount).IsEqualTo(4);
        await Assert.That(result.StopReason).IsEqualTo(DrainStopReason.Failed);
        await Assert.That(client.Receiver.Disposed).IsTrue();
    }

    [Test]
    public async Task Receive_Error_And_Cancellation_Dispose_Session()
    {
        var client = new FakeClient();
        client.SessionReceiver.Receive = (_, _) => throw new InvalidOperationException("private transport failure");
        await using var svc = ServiceBusFixture.Create(client, new());
        var result = await svc.ReceiveMessagesWithSessionAsync("MyQueue", sessionOptions: new(SessionReceiveMode.NextAvailable));
        await Assert.That(result.Status).IsEqualTo(MessageOperationStatus.Failed);
        await Assert.That(result.AcceptedSessionId).IsEqualTo("CaseSensitiveID");
        await Assert.That(client.SessionReceiver.Disposed).IsTrue();
        using var cts = new CancellationTokenSource();
        client.SessionReceiver.Receive = (_, token) => { cts.Cancel(); token.ThrowIfCancellationRequested(); throw new Exception(); };
        var cancelled = await svc.ReceiveMessagesWithSessionAsync("MyQueue", sessionOptions: new(SessionReceiveMode.Specified, "CaseSensitiveID"), cancellationToken: cts.Token);
        await Assert.That(cancelled.Status).IsEqualTo(MessageOperationStatus.Cancelled);
        await Assert.That(cancelled.AcceptedSessionId).IsEqualTo("CaseSensitiveID");
    }

    [Test]
    public async Task Repository_Failure_Is_Not_Reported_As_Missing_Entity()
    {
        var client = new FakeClient();
        await using var svc = ServiceBusFixture.Create(client, new() { Failure = new InvalidOperationException("private SQL error") });
        var received = await svc.ReceiveMessagesWithSessionAsync("MyQueue");
        var drained = await svc.DrainMessagesAsync("MyQueue");
        await Assert.That(received.Status).IsEqualTo(MessageOperationStatus.Failed);
        await Assert.That(drained.StopReason).IsEqualTo(DrainStopReason.Failed);
        await Assert.That(client.OrdinaryCalls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Ordinary_Peek_And_Receive_Dispose_On_Cancellation()
    {
        var client = new FakeClient();
        using var cts = new CancellationTokenSource();
        client.Receiver.Receive = (_, _) => throw new OperationCanceledException(cts.Token);
        await using var svc = ServiceBusFixture.Create(client, new());
        var receiveCancelled = false;
        try { await svc.ReceiveMessagesAsync("MyQueue", cancellationToken: cts.Token); }
        catch (OperationCanceledException) { receiveCancelled = true; }
        await Assert.That(receiveCancelled).IsTrue();
        await Assert.That(client.Receiver.Disposed).IsTrue();
        var peekCancelled = false;
        try { await svc.PeekMessagesAsync("MyQueue", cancellationToken: cts.Token); }
        catch (OperationCanceledException) { peekCancelled = true; }
        await Assert.That(peekCancelled).IsTrue();
        await Assert.That(client.Receiver.Disposed).IsTrue();
    }

    [Test]
    public async Task Client_Factory_Is_Lazy_And_Service_Owns_Disposal()
    {
        var client = new FakeClient();
        var calls = 0;
        var svc = new Services.ServiceBusService("localhost", Microsoft.Extensions.Logging.Abstractions.NullLogger<Services.ServiceBusService>.Instance,
            new FakeEntityRepository(), _ => { calls++; return client; });
        await Assert.That(calls).IsEqualTo(0);
        await svc.DrainMessagesAsync("missing");
        await Assert.That(calls).IsEqualTo(0);
        await svc.DrainMessagesAsync("MyQueue");
        await Assert.That(calls).IsEqualTo(1);
        await svc.DisposeAsync();
        await Assert.That(client.Disposed).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Scoped_Peek_Paginates_Short_Batches_Without_Locking(bool subscription)
    {
        var client = new FakeClient();
        var batches = new Queue<IReadOnlyList<ServiceBusReceivedMessage>>([
            [ServiceBusModelFactory.ServiceBusReceivedMessage(sequenceNumber: 8, sessionId: "casesensitiveid")],
            [ServiceBusModelFactory.ServiceBusReceivedMessage(sequenceNumber: 12, sessionId: "CaseSensitiveID")]]);
        client.Receiver.Peek = (_, _, _) => Task.FromResult(batches.Dequeue());
        await using var svc = ServiceBusFixture.Create(client, new());
        var result = await svc.PeekMessagesWithSessionAsync(subscription ? "MyTopic|MySub" : "MyQueue", 1, "CaseSensitiveID");
        await Assert.That(result.Messages.Single().SequenceNumber).IsEqualTo(12);
        await Assert.That(result.StopReason).IsEqualTo(PeekStopReason.Completed);
        await Assert.That(result.ScannedCount).IsEqualTo(2);
        await Assert.That(client.Receiver.PeekCalls.Select(c => c.From).SequenceEqual(new long?[] { null, 9 })).IsTrue();
        await Assert.That(client.OrdinaryCalls.Single().Path).IsEqualTo(subscription ? "mytopic/subscriptions/mysub" : "myqueue");
        await Assert.That(client.SessionCalls.Count).IsEqualTo(0);
        await Assert.That(client.Receiver.BatchSizes.Count).IsEqualTo(0);
        await Assert.That(client.Receiver.Disposed).IsTrue();
    }

    [Test]
    [Arguments("MyQueue", null, 2L)]
    [Arguments("MyQueue", "CaseSensitiveID", 2L)]
    [Arguments("MyTopic|MySub", null, 2L)]
    [Arguments("MyTopic|MySub", "CaseSensitiveID", 2L)]
    [Arguments("MyQueue/$DeadLetterQueue", null, 2L)]
    [Arguments("MyTopic|MySub/$DeadLetterQueue", "CaseSensitiveID", 2L)]
    [Arguments("missing", null, 2L)]
    [Arguments("", null, 2L)]
    [Arguments(" ", " ", -1L)]
    [Arguments("MyQueue", " ", 2L)]
    [Arguments("MyQueue", null, 0L)]
    [Arguments("MyQueue", null, -1L)]
    public async Task DeadLetter_Result_Is_Unsupported_Repeatedly_Without_Any_IO(string entity, string? sessionId, long sequenceNumber)
    {
        var client = new FakeClient();
        var repo = new FakeEntityRepository();
        var factoryCalls = 0;
        var svc = new Services.ServiceBusService("localhost", Microsoft.Extensions.Logging.Abstractions.NullLogger<Services.ServiceBusService>.Instance,
            repo, _ => { factoryCalls++; return client; });
        await using (svc)
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var result = await svc.DeadLetterMessageWithSessionAsync(entity, sequenceNumber, sessionId);
                await Assert.That(result.Status).IsEqualTo(DeadLetterStatus.Unsupported);
                await Assert.That(result.IsSettled).IsFalse();
                await Assert.That(result.CleanupFailed).IsFalse();
                await AssertNoDeadLetterIO(client, repo, factoryCalls);
            }
        }
        await Assert.That(client.Disposed).IsFalse();
    }

    [Test]
    [Arguments("MyQueue", 2L)]
    [Arguments("MyTopic|MySub", 2L)]
    [Arguments("MyQueue/$DeadLetterQueue", 2L)]
    [Arguments("MyTopic|MySub/$DeadLetterQueue", 2L)]
    [Arguments("missing", 2L)]
    [Arguments("", 2L)]
    [Arguments(" ", -1L)]
    [Arguments("MyQueue", 0L)]
    [Arguments("MyQueue", -1L)]
    public async Task DeadLetter_Legacy_Throws_NotSupported_Repeatedly_Without_Any_IO(string entity, long sequenceNumber)
    {
        var client = new FakeClient();
        var repo = new FakeEntityRepository();
        var factoryCalls = 0;
        var svc = new Services.ServiceBusService("localhost", Microsoft.Extensions.Logging.Abstractions.NullLogger<Services.ServiceBusService>.Instance,
            repo, _ => { factoryCalls++; return client; });
        await using (svc)
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                Exception? failure = null;
                try { await svc.DeadLetterMessageAsync(entity, sequenceNumber); }
                catch (Exception exception) { failure = exception; }
                await Assert.That(failure is NotSupportedException).IsTrue();
                await Assert.That(failure!.Message.Contains("lock", StringComparison.OrdinalIgnoreCase)).IsTrue();
                await AssertNoDeadLetterIO(client, repo, factoryCalls);
            }
        }
        await Assert.That(client.Disposed).IsFalse();
    }

    [Test]
    [Arguments("MyQueue", null)]
    [Arguments("MyQueue", "CaseSensitiveID")]
    [Arguments("MyTopic|MySub", "CaseSensitiveID")]
    [Arguments("MyQueue/$DeadLetterQueue", null)]
    [Arguments("MyTopic|MySub/$DeadLetterQueue", " ")]
    [Arguments("missing", null)]
    [Arguments("", " ")]
    public async Task DeadLetter_PreCancellation_Takes_Precedence_Without_Any_IO(string entity, string? sessionId)
    {
        var client = new FakeClient();
        var repo = new FakeEntityRepository();
        var factoryCalls = 0;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await using var svc = new Services.ServiceBusService("localhost", Microsoft.Extensions.Logging.Abstractions.NullLogger<Services.ServiceBusService>.Instance,
            repo, _ => { factoryCalls++; return client; });
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var result = await svc.DeadLetterMessageWithSessionAsync(entity, -1, sessionId, cancellationToken: cts.Token);
            await Assert.That(result.Status).IsEqualTo(DeadLetterStatus.Cancelled);
            await Assert.That(result.IsSettled).IsFalse();
            await Assert.That(result.CleanupFailed).IsFalse();
            Exception? failure = null;
            try { await svc.DeadLetterMessageAsync(entity, -1, cancellationToken: cts.Token); }
            catch (Exception exception) { failure = exception; }
            await Assert.That(failure is OperationCanceledException).IsTrue();
            await AssertNoDeadLetterIO(client, repo, factoryCalls);
        }
    }

    private static async Task AssertNoDeadLetterIO(FakeClient client, FakeEntityRepository repo, int factoryCalls)
    {
        await Assert.That(repo.Calls).IsEqualTo(0);
        await Assert.That(factoryCalls).IsEqualTo(0);
        await Assert.That(client.OrdinaryCalls.Count).IsEqualTo(0);
        await Assert.That(client.SessionCalls.Count).IsEqualTo(0);
        await Assert.That(client.Receiver.BatchSizes.Count).IsEqualTo(0);
        await Assert.That(client.Receiver.PeekCalls.Count).IsEqualTo(0);
        await Assert.That(client.SessionReceiver.Calls).IsEqualTo(0);
        await Assert.That(client.Receiver.Settled.Count + client.Receiver.Abandoned.Count).IsEqualTo(0);
        await Assert.That(client.SessionReceiver.Settlement.Settled.Count + client.SessionReceiver.Settlement.Abandoned.Count).IsEqualTo(0);
        await Assert.That(client.Receiver.Disposed || client.SessionReceiver.Disposed || client.Disposed).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Scoped_Peek_Limits_Are_Explicitly_Incomplete(bool timeLimit)
    {
        var client = new FakeClient();
        var clock = new FakeClock();
        long sequence = 0;
        client.Receiver.Peek = (size, _, _) =>
        {
            if (timeLimit) clock.Now += TimeSpan.FromSeconds(30);
            return Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>(Enumerable.Range(0, size)
                .Select(_ => ServiceBusModelFactory.ServiceBusReceivedMessage(sequenceNumber: ++sequence, sessionId: "Other")).ToList());
        };
        await using var svc = ServiceBusFixture.Create(client, new(), clock);
        var result = await svc.PeekMessagesWithSessionAsync("MyQueue", 1, "CaseSensitiveID");
        await Assert.That(result.IsComplete).IsFalse();
        await Assert.That(result.StopReason).IsEqualTo(timeLimit ? PeekStopReason.TimeLimit : PeekStopReason.MessageLimit);
        await Assert.That(result.ScannedCount).IsEqualTo(timeLimit ? 100 : 10_000);
        await Assert.That(result.Messages.Count).IsEqualTo(0);
        await Assert.That(client.Receiver.Disposed).IsTrue();
    }

    [Test]
    public async Task Scoped_Peek_Empty_Session_And_Dlq_Are_Ordinary()
    {
        var client = new FakeClient();
        client.Receiver.Peek = (_, _, _) => Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>([]);
        await using var svc = ServiceBusFixture.Create(client, new());
        var empty = await svc.PeekMessagesWithSessionAsync("MyQueue", sessionId: "CaseSensitiveID");
        await Assert.That(empty.IsComplete).IsTrue();
        await Assert.That(empty.Messages.Count).IsEqualTo(0);
        client.Receiver.Peek = (_, _, _) => Task.FromResult(ServiceBusFixture.Messages(1));
        var dlq = await svc.PeekMessagesWithSessionAsync("MyQueue/$DeadLetterQueue", 1, sessionId: " ");
        await Assert.That(dlq.Messages.Count).IsEqualTo(1);
        await Assert.That(client.SessionCalls.Count).IsEqualTo(0);
        await Assert.That(client.Receiver.BatchSizes.Count).IsEqualTo(0);
        await Assert.That(client.OrdinaryCalls.Last().Path).IsEqualTo("myqueue/$deadletterqueue");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Scoped_Peek_Failure_Or_Cancellation_Retains_Partial_Results(bool cancel)
    {
        var client = new FakeClient();
        using var cts = new CancellationTokenSource();
        var calls = 0;
        client.Receiver.Peek = (_, _, token) =>
        {
            if (calls++ == 0) return Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>([
                ServiceBusModelFactory.ServiceBusReceivedMessage(sequenceNumber: 1, sessionId: "CaseSensitiveID")]);
            if (cancel) { cts.Cancel(); token.ThrowIfCancellationRequested(); }
            throw new InvalidOperationException("private details");
        };
        await using var svc = ServiceBusFixture.Create(client, new());
        var result = await svc.PeekMessagesWithSessionAsync("MyQueue", 2, "CaseSensitiveID", cts.Token);
        await Assert.That(result.Messages.Count).IsEqualTo(1);
        await Assert.That(result.IsComplete).IsFalse();
        await Assert.That(result.StopReason).IsEqualTo(cancel ? PeekStopReason.Cancelled : PeekStopReason.Failed);
        await Assert.That(client.Receiver.Disposed).IsTrue();
    }

    [Test]
    public async Task Scoped_Peek_Disposal_Failure_Is_Incomplete()
    {
        var client = new FakeClient();
        client.Receiver.DisposeFailure = new InvalidOperationException("private disposal failure");
        await using var svc = ServiceBusFixture.Create(client, new());
        var result = await svc.PeekMessagesWithSessionAsync("MyQueue", sessionId: "CaseSensitiveID");
        await Assert.That(result.StopReason).IsEqualTo(PeekStopReason.Failed);
        await Assert.That(result.IsComplete).IsFalse();
        await Assert.That(client.Receiver.Disposed).IsTrue();
    }

    [Test]
    public async Task Scoped_Peek_Repository_Failure_Is_Not_Missing_Entity()
    {
        var client = new FakeClient();
        await using var svc = ServiceBusFixture.Create(client, new() { Failure = new InvalidOperationException("private SQL details") });
        var peek = await svc.PeekMessagesWithSessionAsync("MyQueue");
        await Assert.That(peek.StopReason).IsEqualTo(PeekStopReason.Failed);
        await Assert.That(client.OrdinaryCalls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Scoped_Peek_Rejects_Nonadvancing_Pagination()
    {
        var client = new FakeClient();
        client.Receiver.Peek = (_, _, _) => Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>([
            ServiceBusModelFactory.ServiceBusReceivedMessage(sequenceNumber: 7, sessionId: "Other")]);
        await using var svc = ServiceBusFixture.Create(client, new());
        var result = await svc.PeekMessagesWithSessionAsync("MyQueue", sessionId: "CaseSensitiveID");
        await Assert.That(result.StopReason).IsEqualTo(PeekStopReason.Failed);
        await Assert.That(result.ScannedCount).IsEqualTo(2);
        await Assert.That(client.Receiver.Disposed).IsTrue();
    }

    [Test]
    public async Task Scoped_Peek_Rejects_Invalid_Or_Missing_Targets_Without_Receivers()
    {
        var client = new FakeClient();
        await using var svc = ServiceBusFixture.Create(client, new());
        var invalidPeek = await svc.PeekMessagesWithSessionAsync("MyQueue", sessionId: " ");
        var missingPeek = await svc.PeekMessagesWithSessionAsync("missing");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelledPeek = await svc.PeekMessagesWithSessionAsync("MyQueue", cancellationToken: cts.Token);
        await Assert.That(invalidPeek.StopReason).IsEqualTo(PeekStopReason.InvalidSessionId);
        await Assert.That(missingPeek.StopReason).IsEqualTo(PeekStopReason.EntityNotFound);
        await Assert.That(cancelledPeek.StopReason).IsEqualTo(PeekStopReason.Cancelled);
        await Assert.That(client.SessionCalls.Count).IsEqualTo(0);
        await Assert.That(client.OrdinaryCalls.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task Next_Peek_Holds_PeekLock_And_Uses_Ordinary_Exact_Filter(bool subscription, bool empty)
    {
        var client = new FakeClient();
        var repo = new FakeEntityRepository();
        var calls = 0;
        client.Receiver.Peek = (_, _, token) =>
        {
            if (client.SessionReceiver.Disposed) throw new InvalidOperationException("session released before peek");
            token.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>(empty ? [] : calls++ == 0
                ? [ServiceBusModelFactory.ServiceBusReceivedMessage(sequenceNumber: 8, sessionId: "casesensitiveid")]
                : [ServiceBusModelFactory.ServiceBusReceivedMessage(sequenceNumber: 12, sessionId: "CaseSensitiveID")]);
        };
        await using var svc = ServiceBusFixture.Create(client, repo);
        var result = await svc.PeekNextAvailableSessionAsync(subscription ? "SBEMULATORNS:TOPIC:MyTopic|MySub" : "SBEMULATORNS:QUEUE:MyQueue", 1);
        await Assert.That(result.AcceptedSessionId).IsEqualTo("CaseSensitiveID");
        await Assert.That(result.StopReason).IsEqualTo(PeekStopReason.Completed);
        await Assert.That(result.Messages.Count).IsEqualTo(empty ? 0 : 1);
        await Assert.That(result.ScannedCount).IsEqualTo(empty ? 0 : 2);
        await Assert.That(client.SessionCalls.Single().QueueOrTopic).IsEqualTo(subscription ? "mytopic" : "myqueue");
        await Assert.That(client.SessionCalls.Single().Subscription).IsEqualTo(subscription ? "mysub" : null);
        await Assert.That(client.SessionCalls.Single().Mode).IsEqualTo(ServiceBusReceiveMode.PeekLock);
        await Assert.That(client.OrdinaryCalls.Single().Path).IsEqualTo(subscription ? "mytopic/subscriptions/mysub" : "myqueue");
        await Assert.That(client.Receiver.BatchSizes.Count).IsEqualTo(0);
        await Assert.That(client.SessionReceiver.Calls).IsEqualTo(0);
        await Assert.That(client.Receiver.Disposed && client.SessionReceiver.Disposed).IsTrue();
        await Assert.That(client.Receiver.PeekCalls.All(c => c.Token.CanBeCanceled)).IsTrue();
        await Assert.That(repo.Calls).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task New_Session_Operations_Route_Dlq_Ordinarily(bool subscription)
    {
        var client = new FakeClient();
        client.Receiver.Peek = (_, _, _) => Task.FromResult(ServiceBusFixture.Messages(1));
        await using var svc = ServiceBusFixture.Create(client, new());
        var path = subscription ? "MyTopic|MySub/$DeadLetterQueue" : "MyQueue/$DeadLetterQueue";
        var peek = await svc.PeekNextAvailableSessionAsync(path, 1);
        var drain = await svc.DrainAllAvailableSessionsAsync(path);
        await Assert.That(peek.AcceptedSessionId).IsNull();
        await Assert.That(peek.Messages.Count).IsEqualTo(1);
        await Assert.That(drain.StopReason).IsEqualTo(DrainStopReason.Empty);
        await Assert.That(drain.SessionCount).IsEqualTo(0);
        await Assert.That(client.SessionCalls.Count).IsEqualTo(0);
        await Assert.That(client.OrdinaryCalls.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments(ServiceBusFailureReason.ServiceTimeout, PeekStopReason.SessionUnavailable, DrainStopReason.SessionUnavailable)]
    [Arguments(ServiceBusFailureReason.SessionCannotBeLocked, PeekStopReason.SessionLocked, DrainStopReason.SessionLocked)]
    [Arguments(ServiceBusFailureReason.GeneralError, PeekStopReason.Failed, DrainStopReason.Failed)]
    public async Task New_Session_Accept_Failure_Does_Not_Fallback(ServiceBusFailureReason reason, PeekStopReason peekReason, DrainStopReason drainReason)
    {
        var client = new FakeClient { Accept = _ => throw new ServiceBusException("private details", reason) };
        await using var svc = ServiceBusFixture.Create(client, new());
        var peek = await svc.PeekNextAvailableSessionAsync("MyQueue");
        var drain = await svc.DrainAllAvailableSessionsAsync("MyQueue");
        await Assert.That(peek.StopReason).IsEqualTo(peekReason);
        await Assert.That(drain.StopReason).IsEqualTo(drainReason);
        await Assert.That(drain.SessionCount).IsEqualTo(0);
        await Assert.That(client.OrdinaryCalls.Count).IsEqualTo(0);
        await Assert.That(client.SessionCalls.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Next_Peek_Retains_Partial_Results_And_ID_On_Cancel_Or_Error(bool cancel)
    {
        var client = new FakeClient();
        using var cts = new CancellationTokenSource();
        client.Receiver.Peek = (_, _, token) =>
        {
            if (client.Receiver.PeekCalls.Count == 1) return Task.FromResult(ServiceBusFixture.Messages(1));
            if (cancel) { cts.Cancel(); token.ThrowIfCancellationRequested(); }
            throw new InvalidOperationException("private details");
        };
        await using var svc = ServiceBusFixture.Create(client, new());
        var result = await svc.PeekNextAvailableSessionAsync("MyQueue", 2, cts.Token);
        await Assert.That(result.StopReason).IsEqualTo(cancel ? PeekStopReason.Cancelled : PeekStopReason.Failed);
        await Assert.That(result.Messages.Count).IsEqualTo(1);
        await Assert.That(result.AcceptedSessionId).IsEqualTo("CaseSensitiveID");
        await Assert.That(client.Receiver.Disposed && client.SessionReceiver.Disposed).IsTrue();
        await Assert.That(client.SessionReceiver.Calls + client.Receiver.BatchSizes.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Next_Peek_Cleanup_Failure_Keeps_Results_And_Disposes_Both(bool sessionFailure)
    {
        var client = new FakeClient();
        client.Receiver.Peek = (_, _, _) => Task.FromResult(ServiceBusFixture.Messages(1));
        if (sessionFailure) client.SessionReceiver.Settlement.DisposeFailure = new InvalidOperationException();
        else client.Receiver.DisposeFailure = new InvalidOperationException();
        await using var svc = ServiceBusFixture.Create(client, new());
        var result = await svc.PeekNextAvailableSessionAsync("MyQueue", 1);
        await Assert.That(result.StopReason).IsEqualTo(PeekStopReason.Failed);
        await Assert.That(result.Messages.Count).IsEqualTo(1);
        await Assert.That(result.AcceptedSessionId).IsEqualTo("CaseSensitiveID");
        await Assert.That(client.Receiver.Disposed && client.SessionReceiver.Disposed).IsTrue();
    }

    [Test]
    public async Task Next_Peek_Uses_Global_Scan_Cap_And_Deadline_Including_Accept()
    {
        var client = new FakeClient();
        var clock = new FakeClock();
        client.Accept = _ => { clock.Now += TimeSpan.FromSeconds(29); return Task.FromResult<ServiceBusSessionReceiver>(client.SessionReceiver); };
        client.Receiver.Peek = (_, _, _) => { clock.Now += TimeSpan.FromSeconds(1); return Task.FromResult(ServiceBusFixture.Messages(1)); };
        await using var svc = ServiceBusFixture.Create(client, new(), clock);
        var timed = await svc.PeekNextAvailableSessionAsync("MyQueue", 2);
        await Assert.That(timed.StopReason).IsEqualTo(PeekStopReason.TimeLimit);
        await Assert.That(timed.Messages.Count).IsEqualTo(1);
        client.Accept = null;
        long seq = 0;
        client.Receiver.Peek = (size, _, _) => Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>(Enumerable.Range(0, size)
            .Select(_ => ServiceBusModelFactory.ServiceBusReceivedMessage(sequenceNumber: ++seq, sessionId: "Other")).ToList());
        var capped = await svc.PeekNextAvailableSessionAsync("MyQueue", 1);
        await Assert.That(capped.StopReason).IsEqualTo(PeekStopReason.MessageLimit);
        await Assert.That(capped.ScannedCount).IsEqualTo(10000);
        await Assert.That(capped.AcceptedSessionId).IsEqualTo("CaseSensitiveID");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task All_Drain_Visits_Two_Sessions_With_Short_Batches(bool subscription)
    {
        var client = new FakeClient();
        var repo = new FakeEntityRepository();
        var first = new FakeSessionReceiver { Id = "First" };
        var second = new FakeSessionReceiver { Id = "Second" };
        var a = new Queue<int>([2, 3, 0]);
        var b = new Queue<int>([1, 0]);
        first.Receive = (_, _) => Task.FromResult(ServiceBusFixture.Messages(a.Dequeue()));
        second.Receive = (_, _) => Task.FromResult(ServiceBusFixture.Messages(b.Dequeue()));
        client.Accept = _ =>
        {
            if (client.SessionCalls.Count == 2 && !first.Disposed) throw new InvalidOperationException("previous session not released");
            return client.SessionCalls.Count switch
            {
                1 => Task.FromResult<ServiceBusSessionReceiver>(first),
                2 => Task.FromResult<ServiceBusSessionReceiver>(second),
                _ => throw new ServiceBusException("none", ServiceBusFailureReason.ServiceTimeout)
            };
        };
        await using var svc = ServiceBusFixture.Create(client, repo);
        var result = await svc.DrainAllAvailableSessionsAsync(subscription ? "MyTopic|MySub" : "MyQueue");
        await Assert.That(result.DeletedCount).IsEqualTo(6);
        await Assert.That(result.SessionCount).IsEqualTo(2);
        await Assert.That(result.StopReason).IsEqualTo(DrainStopReason.SessionUnavailable);
        await Assert.That(first.Disposed && second.Disposed).IsTrue();
        await Assert.That(client.SessionCalls.All(c => c.Mode == ServiceBusReceiveMode.ReceiveAndDelete && c.Token.CanBeCanceled)).IsTrue();
        await Assert.That(client.SessionCalls.All(c => c.Subscription == (subscription ? "mysub" : null))).IsTrue();
        await Assert.That(first.Tokens.Concat(second.Tokens).Distinct().Count()).IsEqualTo(1);
        await Assert.That(repo.Calls).IsEqualTo(1);
        await Assert.That(client.OrdinaryCalls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task All_Drain_Global_Message_Cap_Across_Sessions()
    {
        var client = new FakeClient();
        var first = new FakeSessionReceiver { Id = "First" };
        var second = new FakeSessionReceiver { Id = "Second" };
        first.Receive = (size, _) => Task.FromResult(ServiceBusFixture.Messages(first.Calls <= 60 ? size : 0));
        second.Receive = (size, _) => Task.FromResult(ServiceBusFixture.Messages(size));
        client.Accept = _ => Task.FromResult<ServiceBusSessionReceiver>(client.SessionCalls.Count == 1 ? first : second);
        await using var svc = ServiceBusFixture.Create(client, new());
        var result = await svc.DrainAllAvailableSessionsAsync("MyQueue");
        await Assert.That(result.DeletedCount).IsEqualTo(10000);
        await Assert.That(result.SessionCount).IsEqualTo(2);
        await Assert.That(result.StopReason).IsEqualTo(DrainStopReason.MessageLimit);
        await Assert.That(second.Calls).IsEqualTo(40);
        await Assert.That(client.SessionCalls.Count).IsEqualTo(2);
        await Assert.That(first.Disposed && second.Disposed).IsTrue();
    }

    [Test]
    public async Task All_Drain_Global_Time_Budget_Includes_Accept_And_Previous_Session()
    {
        var client = new FakeClient();
        var clock = new FakeClock();
        var first = new FakeSessionReceiver { Id = "First" };
        var second = new FakeSessionReceiver { Id = "Second" };
        first.Receive = (_, _) => { clock.Now += TimeSpan.FromSeconds(10); return Task.FromResult(ServiceBusFixture.Messages(first.Calls == 1 ? 3 : 0)); };
        client.Accept = _ => { clock.Now += TimeSpan.FromSeconds(5); return Task.FromResult<ServiceBusSessionReceiver>(client.SessionCalls.Count == 1 ? first : second); };
        await using var svc = ServiceBusFixture.Create(client, new(), clock);
        var result = await svc.DrainAllAvailableSessionsAsync("MyQueue");
        await Assert.That(result.StopReason).IsEqualTo(DrainStopReason.TimeLimit);
        await Assert.That(result.DeletedCount).IsEqualTo(3);
        await Assert.That(result.SessionCount).IsEqualTo(2);
        await Assert.That(second.Calls).IsEqualTo(0);
        await Assert.That(first.Disposed && second.Disposed).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task All_Drain_Partial_Failure_Or_Cancellation_Preserves_Progress(bool cancel)
    {
        var client = new FakeClient();
        using var cts = new CancellationTokenSource();
        client.SessionReceiver.Receive = (_, token) =>
        {
            if (client.SessionReceiver.Calls == 1) return Task.FromResult(ServiceBusFixture.Messages(7));
            if (cancel) { cts.Cancel(); token.ThrowIfCancellationRequested(); }
            throw new ServiceBusException("transport timeout", ServiceBusFailureReason.ServiceTimeout);
        };
        await using var svc = ServiceBusFixture.Create(client, new());
        var result = await svc.DrainAllAvailableSessionsAsync("MyQueue", cts.Token);
        await Assert.That(result.DeletedCount).IsEqualTo(7);
        await Assert.That(result.SessionCount).IsEqualTo(1);
        await Assert.That(result.StopReason).IsEqualTo(cancel ? DrainStopReason.Cancelled : DrainStopReason.Failed);
        await Assert.That(client.SessionReceiver.Disposed).IsTrue();
        await Assert.That(client.SessionCalls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task All_Drain_Repeated_Empty_Session_Stops_Without_Destructive_Retry()
    {
        var client = new FakeClient();
        client.SessionReceiver.Receive = (_, _) => Task.FromResult(ServiceBusFixture.Messages(0));
        await using var svc = ServiceBusFixture.Create(client, new());
        var result = await svc.DrainAllAvailableSessionsAsync("MyQueue");
        await Assert.That(result.StopReason).IsEqualTo(DrainStopReason.NoProgress);
        await Assert.That(result.DeletedCount).IsEqualTo(0);
        await Assert.That(result.SessionCount).IsEqualTo(2);
        await Assert.That(client.SessionCalls.Count).IsEqualTo(2);
        await Assert.That(client.SessionReceiver.Calls).IsEqualTo(1);
        await Assert.That(client.SessionReceiver.Disposed).IsTrue();
    }

    [Test]
    public async Task New_Session_Accept_Deadline_Is_Five_Seconds_And_Cancellation_Is_Linked()
    {
        var client = new FakeClient { Accept = async token => { await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new Exception(); } };
        await using var svc = ServiceBusFixture.Create(client, new());
        var peekTask = svc.PeekNextAvailableSessionAsync("MyQueue");
        var drainTask = svc.DrainAllAvailableSessionsAsync("MyQueue");
        var peek = await peekTask.WaitAsync(TimeSpan.FromSeconds(10));
        var drain = await drainTask.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(peek.StopReason).IsEqualTo(PeekStopReason.SessionUnavailable);
        await Assert.That(drain.StopReason).IsEqualTo(DrainStopReason.SessionUnavailable);
        using var cts = new CancellationTokenSource();
        client.Accept = token => { cts.Cancel(); token.ThrowIfCancellationRequested(); throw new Exception(); };
        var cancelledPeek = await svc.PeekNextAvailableSessionAsync("MyQueue", cancellationToken: cts.Token);
        using var drainCts = new CancellationTokenSource();
        client.Accept = token => { drainCts.Cancel(); token.ThrowIfCancellationRequested(); throw new Exception(); };
        var cancelledDrain = await svc.DrainAllAvailableSessionsAsync("MyQueue", drainCts.Token);
        await Assert.That(cancelledPeek.StopReason).IsEqualTo(PeekStopReason.Cancelled);
        await Assert.That(cancelledDrain.StopReason).IsEqualTo(DrainStopReason.Cancelled);
        await Assert.That(client.OrdinaryCalls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task New_Session_Operations_Disposal_Is_Bounded_And_Keeps_Progress()
    {
        var client = new FakeClient();
        client.Receiver.Peek = (_, _, _) => Task.FromResult(ServiceBusFixture.Messages(1));
        client.SessionReceiver.Dispose = () => new ValueTask(new TaskCompletionSource().Task);
        await using var svc = ServiceBusFixture.Create(client, new());
        var peek = await svc.PeekNextAvailableSessionAsync("MyQueue", 1).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(peek.StopReason).IsEqualTo(PeekStopReason.Failed);
        await Assert.That(peek.Messages.Count).IsEqualTo(1);
        await Assert.That(peek.AcceptedSessionId).IsEqualTo("CaseSensitiveID");
        client.SessionReceiver.Receive = (_, _) => Task.FromResult(ServiceBusFixture.Messages(client.SessionReceiver.Calls == 1 ? 3 : 0));
        var drain = await svc.DrainAllAvailableSessionsAsync("MyQueue").WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(drain.StopReason).IsEqualTo(DrainStopReason.Failed);
        await Assert.That(drain.DeletedCount).IsEqualTo(3);
        await Assert.That(drain.SessionCount).IsEqualTo(1);
        await Assert.That(client.SessionCalls.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task New_Session_Operations_Reject_Missing_Or_Failed_Repository(bool failed)
    {
        var client = new FakeClient();
        await using var svc = ServiceBusFixture.Create(client, new() { Failure = failed ? new InvalidOperationException("private SQL error") : null });
        var peek = await svc.PeekNextAvailableSessionAsync("missing");
        var drain = await svc.DrainAllAvailableSessionsAsync("missing");
        await Assert.That(peek.StopReason).IsEqualTo(failed ? PeekStopReason.Failed : PeekStopReason.EntityNotFound);
        await Assert.That(drain.StopReason).IsEqualTo(failed ? DrainStopReason.Failed : DrainStopReason.EntityNotFound);
        await Assert.That(client.SessionCalls.Count + client.OrdinaryCalls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task New_Session_Operations_PreCancellation_And_Invalid_Peek_Count_Create_No_Receiver()
    {
        var client = new FakeClient();
        var repo = new FakeEntityRepository();
        await using var svc = ServiceBusFixture.Create(client, repo);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var peek = await svc.PeekNextAvailableSessionAsync("MyQueue", cancellationToken: cts.Token);
        var drain = await svc.DrainAllAvailableSessionsAsync("MyQueue", cts.Token);
        var invalid = await svc.PeekNextAvailableSessionAsync("MyQueue", 0);
        await Assert.That(peek.StopReason).IsEqualTo(PeekStopReason.Cancelled);
        await Assert.That(drain.StopReason).IsEqualTo(DrainStopReason.Cancelled);
        await Assert.That(invalid.StopReason).IsEqualTo(PeekStopReason.Failed);
        await Assert.That(repo.Calls).IsEqualTo(0);
        await Assert.That(client.SessionCalls.Count + client.OrdinaryCalls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Next_Peek_Accept_Exhausting_Overall_Deadline_Does_Not_Inspect()
    {
        var client = new FakeClient();
        var clock = new FakeClock();
        client.Accept = _ => { clock.Now += TimeSpan.FromSeconds(30); return Task.FromResult<ServiceBusSessionReceiver>(client.SessionReceiver); };
        await using var svc = ServiceBusFixture.Create(client, new(), clock);
        var result = await svc.PeekNextAvailableSessionAsync("MyQueue");
        await Assert.That(result.StopReason).IsEqualTo(PeekStopReason.TimeLimit);
        await Assert.That(result.AcceptedSessionId).IsEqualTo("CaseSensitiveID");
        await Assert.That(client.Receiver.PeekCalls.Count + client.SessionReceiver.Calls).IsEqualTo(0);
        await Assert.That(client.SessionReceiver.Disposed).IsTrue();
    }

    [Test]
    [Arguments(ServiceBusFailureReason.ServiceTimeout, DrainStopReason.SessionUnavailable)]
    [Arguments(ServiceBusFailureReason.SessionCannotBeLocked, DrainStopReason.SessionLocked)]
    [Arguments(ServiceBusFailureReason.GeneralError, DrainStopReason.Failed)]
    public async Task All_Drain_Second_Accept_Failure_Preserves_First_Session_Progress(ServiceBusFailureReason failure, DrainStopReason expected)
    {
        var client = new FakeClient();
        client.SessionReceiver.Receive = (_, _) => Task.FromResult(ServiceBusFixture.Messages(client.SessionReceiver.Calls == 1 ? 4 : 0));
        client.Accept = _ => client.SessionCalls.Count == 1 ? Task.FromResult<ServiceBusSessionReceiver>(client.SessionReceiver)
            : throw new ServiceBusException("private transport details", failure);
        await using var svc = ServiceBusFixture.Create(client, new());
        var result = await svc.DrainAllAvailableSessionsAsync("MyQueue");
        await Assert.That(result.StopReason).IsEqualTo(expected);
        await Assert.That(result.DeletedCount).IsEqualTo(4);
        await Assert.That(result.SessionCount).IsEqualTo(1);
        await Assert.That(client.SessionReceiver.Disposed).IsTrue();
        await Assert.That(client.SessionCalls.Count).IsEqualTo(2);
    }

    [Test]
    public async Task All_Drain_Repeated_Previously_Productive_Session_Can_Progress_Once_More()
    {
        var client = new FakeClient();
        var batches = new Queue<int>([3, 0, 2, 0, 0]);
        client.SessionReceiver.Receive = (_, _) => Task.FromResult(ServiceBusFixture.Messages(batches.Dequeue()));
        await using var svc = ServiceBusFixture.Create(client, new());
        var result = await svc.DrainAllAvailableSessionsAsync("MyQueue");
        await Assert.That(result.StopReason).IsEqualTo(DrainStopReason.NoProgress);
        await Assert.That(result.DeletedCount).IsEqualTo(5);
        await Assert.That(result.SessionCount).IsEqualTo(4);
        await Assert.That(client.SessionReceiver.Calls).IsEqualTo(5);
        await Assert.That(client.SessionReceiver.Disposed).IsTrue();
    }

    [Test]
    public async Task All_Drain_Final_Request_Uses_Only_Remaining_Global_Message_Budget()
    {
        var client = new FakeClient();
        var first = new FakeSessionReceiver { Id = "First" };
        var second = new FakeSessionReceiver { Id = "Second" };
        first.Receive = (_, _) => Task.FromResult(ServiceBusFixture.Messages(first.Calls == 1 ? 3 : 0));
        second.Receive = (size, _) => Task.FromResult(ServiceBusFixture.Messages(size));
        client.Accept = _ => Task.FromResult<ServiceBusSessionReceiver>(client.SessionCalls.Count == 1 ? first : second);
        await using var svc = ServiceBusFixture.Create(client, new());
        var result = await svc.DrainAllAvailableSessionsAsync("MyQueue");
        await Assert.That(result.StopReason).IsEqualTo(DrainStopReason.MessageLimit);
        await Assert.That(result.DeletedCount).IsEqualTo(10000);
        await Assert.That(second.BatchSizes.Last()).IsEqualTo(97);
        await Assert.That(result.SessionCount).IsEqualTo(2);
        await Assert.That(first.Disposed && second.Disposed).IsTrue();
    }

    [Test]
    public async Task All_Drain_Immediate_Disposal_Failure_Does_Not_Accept_Again()
    {
        var client = new FakeClient();
        client.SessionReceiver.Receive = (_, _) => Task.FromResult(ServiceBusFixture.Messages(client.SessionReceiver.Calls == 1 ? 4 : 0));
        client.SessionReceiver.Settlement.DisposeFailure = new InvalidOperationException("private dispose failure");
        await using var svc = ServiceBusFixture.Create(client, new());
        var result = await svc.DrainAllAvailableSessionsAsync("MyQueue");
        await Assert.That(result.StopReason).IsEqualTo(DrainStopReason.Failed);
        await Assert.That(result.DeletedCount).IsEqualTo(4);
        await Assert.That(result.SessionCount).IsEqualTo(1);
        await Assert.That(client.SessionCalls.Count).IsEqualTo(1);
        await Assert.That(client.SessionReceiver.Disposed).IsTrue();
    }

    [Test]
    public async Task CleanEntityNameForComparison_Lowercases()
    {
        var input = "MyQueue";
        var comp = Services.ServiceBusService.CleanEntityNameForComparison(input);
        await Assert.That(comp).IsEqualTo("myqueue");
    }
}

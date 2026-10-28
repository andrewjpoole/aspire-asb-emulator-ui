using Azure.Messaging.ServiceBus;
using AspireAsbEmulatorUi.App.Models;
using AspireAsbEmulatorUi.App.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AspireAsbEmulatorUi.App.Tests;

internal sealed class FakeEntityRepository : AsbEmulatorSqlEntityRepository
{
    public int Calls { get; private set; }
    public Exception? Failure { get; set; }
    public override Task<List<ServiceBusEntityInfo>> GetEntitiesAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        cancellationToken.ThrowIfCancellationRequested();
        if (Failure is not null) throw Failure;
        return Task.FromResult(new List<ServiceBusEntityInfo>
        {
            new() { Name = "MyQueue", EntityType = "Queue" },
            new() { Name = "MyTopic|MySub", EntityType = "Subscription" }
        });
    }
}

internal sealed class FakeReceiver : ServiceBusReceiver
{
    public Func<int, CancellationToken, Task<IReadOnlyList<ServiceBusReceivedMessage>>> Receive { get; set; }
        = (_, _) => Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>([]);
    public List<int> BatchSizes { get; } = [];
    public List<TimeSpan?> Waits { get; } = [];
    public List<CancellationToken> Tokens { get; } = [];
    public bool Disposed { get; private set; }
    public Exception? DisposeFailure { get; set; }
    public Func<ValueTask>? Dispose { get; set; }
    public int DisposeCalls { get; private set; }
    public Func<ServiceBusReceivedMessage, CancellationToken, Task>? Settle { get; set; }
    public List<ServiceBusReceivedMessage> CompletedMessages { get; } = [];
    public List<ServiceBusReceivedMessage> DeadLetteredMessages { get; } = [];
    public override Task CompleteMessageAsync(ServiceBusReceivedMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CompletedMessages.Add(message);
        return Settle?.Invoke(message, cancellationToken) ?? Task.CompletedTask;
    }
    public override Task<IReadOnlyList<ServiceBusReceivedMessage>> ReceiveMessagesAsync(int maxMessages, TimeSpan? maxWaitTime = null, CancellationToken cancellationToken = default)
    {
        BatchSizes.Add(maxMessages);
        Waits.Add(maxWaitTime);
        Tokens.Add(cancellationToken);
        return Receive(maxMessages, cancellationToken);
    }
    public Func<int, long?, CancellationToken, Task<IReadOnlyList<ServiceBusReceivedMessage>>>? Peek { get; set; }
    public List<(int Size, long? From, CancellationToken Token)> PeekCalls { get; } = [];
    public override Task<IReadOnlyList<ServiceBusReceivedMessage>> PeekMessagesAsync(int maxMessages, long? fromSequenceNumber = null, CancellationToken cancellationToken = default)
    {
        PeekCalls.Add((maxMessages, fromSequenceNumber, cancellationToken));
        return Peek?.Invoke(maxMessages, fromSequenceNumber, cancellationToken) ?? Receive(maxMessages, cancellationToken);
    }
    public List<long> Settled { get; } = [];
    public List<long> Abandoned { get; } = [];
    public override Task DeadLetterMessageAsync(ServiceBusReceivedMessage message, string deadLetterReason, string? deadLetterErrorDescription = default, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Settled.Add(message.SequenceNumber);
        DeadLetteredMessages.Add(message);
        return Settle?.Invoke(message, cancellationToken) ?? Task.CompletedTask;
    }
    public override Task AbandonMessageAsync(ServiceBusReceivedMessage message, IDictionary<string, object>? propertiesToModify = default, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Abandoned.Add(message.SequenceNumber);
        return Task.CompletedTask;
    }
    public override ValueTask DisposeAsync()
    {
        Disposed = true;
        DisposeCalls++;
        return Dispose?.Invoke() ?? (DisposeFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(DisposeFailure));
    }
}

internal sealed class FakeSessionReceiver : ServiceBusSessionReceiver
{
    public string Id { get; set; } = "CaseSensitiveID";
    public override string SessionId => Id;
    public DateTimeOffset LockDeadline { get; set; } = DateTimeOffset.UtcNow.AddMinutes(1);
    public override DateTimeOffset SessionLockedUntil => LockDeadline;
    public List<int> BatchSizes { get; } = [];
    public List<CancellationToken> Tokens { get; } = [];
    public Func<ValueTask>? Dispose { get; set; }
    public Func<int, CancellationToken, Task<IReadOnlyList<ServiceBusReceivedMessage>>> Receive { get; set; }
        = (_, _) => Task.FromResult(ServiceBusFixture.Messages(1));
    public bool Disposed { get; private set; }
    public int Calls { get; private set; }
    public override Task<IReadOnlyList<ServiceBusReceivedMessage>> ReceiveMessagesAsync(int maxMessages, TimeSpan? maxWaitTime = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        BatchSizes.Add(maxMessages);
        Tokens.Add(cancellationToken);
        return Receive(maxMessages, cancellationToken);
    }
    public FakeReceiver Settlement { get; } = new();
    public override Task CompleteMessageAsync(ServiceBusReceivedMessage message, CancellationToken cancellationToken = default)
        => Settlement.CompleteMessageAsync(message, cancellationToken);
    public override Task DeadLetterMessageAsync(ServiceBusReceivedMessage message, string deadLetterReason, string? deadLetterErrorDescription = default, CancellationToken cancellationToken = default)
        => Settlement.DeadLetterMessageAsync(message, deadLetterReason, deadLetterErrorDescription, cancellationToken);
    public override Task AbandonMessageAsync(ServiceBusReceivedMessage message, IDictionary<string, object>? propertiesToModify = default, CancellationToken cancellationToken = default)
        => Settlement.AbandonMessageAsync(message, propertiesToModify, cancellationToken);
    public override ValueTask DisposeAsync() { Disposed = true; return Dispose?.Invoke() ?? Settlement.DisposeAsync(); }
}

internal sealed class FakeSender : ServiceBusSender
{
    public List<ServiceBusMessage> Messages { get; } = [];
    public Func<ServiceBusMessage, CancellationToken, Task>? Send { get; set; }
    public Func<ValueTask>? Dispose { get; set; }
    public int DisposeCalls { get; private set; }
    public override Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default)
    {
        Messages.Add(message);
        return Send?.Invoke(message, cancellationToken) ?? Task.CompletedTask;
    }
    public override ValueTask DisposeAsync()
    {
        DisposeCalls++;
        return Dispose?.Invoke() ?? ValueTask.CompletedTask;
    }
}

internal sealed class FakeClient : ServiceBusClient
{
    public FakeSessionReceiver SessionReceiver { get; } = new();
    public Func<CancellationToken, Task<ServiceBusSessionReceiver>>? Accept { get; set; }
    public List<(string QueueOrTopic, string? Subscription, string? Id, ServiceBusReceiveMode Mode, CancellationToken Token)> SessionCalls { get; } = [];
    public List<int> PrefetchCounts { get; } = [];
    private Task<ServiceBusSessionReceiver> AcceptCore(string path, string? sub, string? id, ServiceBusSessionReceiverOptions options, CancellationToken token)
    {
        SessionCalls.Add((path, sub, id, options.ReceiveMode, token));
        PrefetchCounts.Add(options.PrefetchCount);
        return Accept?.Invoke(token) ?? Task.FromResult<ServiceBusSessionReceiver>(SessionReceiver);
    }
    public override Task<ServiceBusSessionReceiver> AcceptNextSessionAsync(string queueName, ServiceBusSessionReceiverOptions options = default!, CancellationToken cancellationToken = default)
        => AcceptCore(queueName, null, null, options, cancellationToken);
    public override Task<ServiceBusSessionReceiver> AcceptNextSessionAsync(string topicName, string subscriptionName, ServiceBusSessionReceiverOptions options = default!, CancellationToken cancellationToken = default)
        => AcceptCore(topicName, subscriptionName, null, options, cancellationToken);
    public override Task<ServiceBusSessionReceiver> AcceptSessionAsync(string queueName, string sessionId, ServiceBusSessionReceiverOptions options = default!, CancellationToken cancellationToken = default)
        => AcceptCore(queueName, null, sessionId, options, cancellationToken);
    public override Task<ServiceBusSessionReceiver> AcceptSessionAsync(string topicName, string subscriptionName, string sessionId, ServiceBusSessionReceiverOptions options = default!, CancellationToken cancellationToken = default)
        => AcceptCore(topicName, subscriptionName, sessionId, options, cancellationToken);
    public FakeSender Sender { get; } = new();
    public List<string> SenderPaths { get; } = [];
    public Exception? SenderCreationFailure { get; set; }
    public override ServiceBusSender CreateSender(string queueOrTopicName)
    {
        SenderPaths.Add(queueOrTopicName);
        if (SenderCreationFailure is not null) throw SenderCreationFailure;
        return Sender;
    }
    public FakeReceiver Receiver { get; } = new();
    public List<(string Path, ServiceBusReceiveMode Mode)> OrdinaryCalls { get; } = [];
    public bool Disposed { get; private set; }
    public override ServiceBusReceiver CreateReceiver(string queueName, ServiceBusReceiverOptions options)
    {
        OrdinaryCalls.Add((queueName, options.ReceiveMode));
        PrefetchCounts.Add(options.PrefetchCount);
        return Receiver;
    }
    public override ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal static class ServiceBusFixture
{
    public static ServiceBusService Create(FakeClient client, FakeEntityRepository repo, TimeProvider? clock = null)
        => new("localhost", NullLogger<ServiceBusService>.Instance, repo, _ => client, clock);
    public static IReadOnlyList<ServiceBusReceivedMessage> Messages(int count)
        => Enumerable.Range(0, count).Select(i => ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString("body"), messageId: $"message-{i}", sessionId: "CaseSensitiveID")).ToList();
}

internal sealed class FakeClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}

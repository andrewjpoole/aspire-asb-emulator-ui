using AspireAsbEmulatorUi.App.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AspireAsbEmulatorUi.App.Tests;

public class ReceivedMessageCircuitHandlerTests
{
    [Test]
    public async Task DisconnectAndClose_NotifyOwnersWithoutOneFailureBlockingOthers()
    {
        var handler = new ReceivedMessageCircuitHandler(NullLogger<ReceivedMessageCircuitHandler>.Instance);
        var calls = 0;
        handler.Disconnected += () => Task.FromException(new InvalidOperationException("cleanup failed"));
        handler.Disconnected += () => { calls++; return Task.CompletedTask; };
        await handler.OnConnectionDownAsync(null!, CancellationToken.None);
        await handler.OnCircuitClosedAsync(null!, CancellationToken.None);
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task UnsubscribedViewer_IsNotCalledOnDisconnect()
    {
        var handler = new ReceivedMessageCircuitHandler(NullLogger<ReceivedMessageCircuitHandler>.Instance);
        var called = false;
        Func<Task> callback = () => { called = true; return Task.CompletedTask; };
        handler.Disconnected += callback;
        handler.Disconnected -= callback;
        await handler.OnConnectionDownAsync(null!, CancellationToken.None);
        await Assert.That(called).IsFalse();
    }
}

using Microsoft.AspNetCore.Components.Server.Circuits;

namespace AspireAsbEmulatorUi.App.Services;

/// <summary>Notifies viewer owners to release received-message locks when their circuit disconnects.</summary>
public sealed class ReceivedMessageCircuitHandler(ILogger<ReceivedMessageCircuitHandler> logger) : CircuitHandler
{
    internal event Func<Task>? Disconnected;

    /// <inheritdoc />
    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken) => ReleaseViewersAsync();

    /// <inheritdoc />
    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken) => ReleaseViewersAsync();

    private async Task ReleaseViewersAsync()
    {
        if (Disconnected is not { } callbacks) return;
        foreach (var callback in callbacks.GetInvocationList().Cast<Func<Task>>())
        {
            try { await callback(); }
            catch (Exception ex) { logger.LogWarning(ex, "Could not release a viewer's received-message locks after disconnect"); }
        }
    }
}

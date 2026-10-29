using AspireAsbEmulatorUi.App.Models;

namespace AspireAsbEmulatorUi.App.Tests;

public class ViewerSessionSelectionTests
{
    [Test]
    public async Task Receive_NormalizesAllToNextSessionButPeekAndClearAllowAll()
    {
        await Assert.That(ViewerSessionSelection.Normalize(ViewerMode.Receive, ViewerSessionScope.All))
            .IsEqualTo(ViewerSessionScope.NextAvailableSession);
        await Assert.That(ViewerSessionSelection.Normalize(ViewerMode.Peek, ViewerSessionScope.All))
            .IsEqualTo(ViewerSessionScope.All);
        await Assert.That(ViewerSessionSelection.Normalize(ViewerMode.Clear, ViewerSessionScope.All))
            .IsEqualTo(ViewerSessionScope.All);
    }

    [Test]
    public async Task ModeChanges_PreserveSpecifiedSessionScope()
    {
        foreach (var mode in Enum.GetValues<ViewerMode>())
            await Assert.That(ViewerSessionSelection.Normalize(mode, ViewerSessionScope.SessionId))
                .IsEqualTo(ViewerSessionScope.SessionId);
        var options = ViewerSessionSelection.ReceiverOptions(true, ViewerSessionScope.SessionId, "CaseSensitive-A");
        await Assert.That(options.Mode).IsEqualTo(SessionReceiveMode.Specified);
        await Assert.That(options.SessionId).IsEqualTo("CaseSensitive-A");
    }

    [Test]
    public async Task SessionControls_RespectKnownUnknownAndDlqConfiguration()
    {
        await Assert.That(ViewerSessionSelection.UsesSessions(true, false, false)).IsTrue();
        await Assert.That(ViewerSessionSelection.UsesSessions(null, false, false)).IsFalse();
        await Assert.That(ViewerSessionSelection.UsesSessions(null, true, false)).IsTrue();
        await Assert.That(ViewerSessionSelection.UsesSessions(false, true, false)).IsFalse();
        await Assert.That(ViewerSessionSelection.UsesSessions(true, true, true)).IsFalse();
    }

    [Test]
    public async Task OrdinaryAndDlqRouting_RemainStandardRegardlessSessionScope()
    {
        foreach (var scope in Enum.GetValues<ViewerSessionScope>())
            await Assert.That(ViewerSessionSelection.ReceiverOptions(false, scope, "unused").Mode)
                .IsEqualTo(SessionReceiveMode.Standard);
        await Assert.That(ViewerSessionSelection.ReceiverOptions(true, ViewerSessionScope.NextAvailableSession, "").Mode)
            .IsEqualTo(SessionReceiveMode.NextAvailable);
    }

    [Test]
    public async Task DlqReceiveAndClear_PreserveOwnedBatchButOtherContextChangesDoNot()
    {
        foreach (var previous in Enum.GetValues<ViewerMode>())
        foreach (var next in Enum.GetValues<ViewerMode>())
        {
            await Assert.That(ViewerSessionSelection.PreserveReceivedBatch(true, previous, next))
                .IsEqualTo(previous != ViewerMode.Peek && next != ViewerMode.Peek);
            await Assert.That(ViewerSessionSelection.PreserveReceivedBatch(false, previous, next)).IsFalse();
        }
    }

    [Test]
    public async Task ReceiveConfirmation_OnlyRequiredForUnsettledLocks()
    {
        await Assert.That(ViewerSessionSelection.RequiresReceiveConfirmation([])).IsFalse();
        foreach (var state in Enum.GetValues<LockedMessageState>())
            await Assert.That(ViewerSessionSelection.RequiresReceiveConfirmation([state]))
                .IsEqualTo(state is LockedMessageState.Locked or LockedMessageState.Settling or LockedMessageState.Unknown);
        await Assert.That(ViewerSessionSelection.RequiresReceiveConfirmation(
            [LockedMessageState.Completed, LockedMessageState.DeadLettered, LockedMessageState.Expired])).IsFalse();
        await Assert.That(ViewerSessionSelection.RequiresReceiveConfirmation(
            [LockedMessageState.Completed, LockedMessageState.Locked])).IsTrue();
    }

    [Test]
    public async Task AllScope_CannotBePassedToSingleSessionReceiver()
    {
        await Assert.That(() => ViewerSessionSelection.ReceiverOptions(true, ViewerSessionScope.All, ""))
            .Throws<InvalidOperationException>();
    }
}

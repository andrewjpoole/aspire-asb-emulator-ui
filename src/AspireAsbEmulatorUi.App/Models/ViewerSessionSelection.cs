namespace AspireAsbEmulatorUi.App.Models;

internal enum ViewerMode { Peek, Receive, Clear }
internal enum ViewerSessionScope { All, NextAvailableSession, SessionId }

internal static class ViewerSessionSelection
{
    internal static bool UsesSessions(bool? requiresSession, bool manuallyEnabled, bool isDlq) =>
        !isDlq && (requiresSession == true || (requiresSession is null && manuallyEnabled));

    internal static bool PreserveReceivedBatch(bool isDlq, ViewerMode previousMode, ViewerMode nextMode) =>
        isDlq && previousMode is ViewerMode.Receive or ViewerMode.Clear && nextMode is ViewerMode.Receive or ViewerMode.Clear;

    internal static ViewerSessionScope Normalize(ViewerMode mode, ViewerSessionScope scope) =>
        mode == ViewerMode.Receive && scope == ViewerSessionScope.All
            ? ViewerSessionScope.NextAvailableSession
            : scope;

    internal static SessionReceiveOptions ReceiverOptions(bool usesSessions, ViewerSessionScope scope, string sessionId) =>
        !usesSessions ? new() : scope switch
        {
            ViewerSessionScope.NextAvailableSession => new(SessionReceiveMode.NextAvailable),
            ViewerSessionScope.SessionId => new(SessionReceiveMode.Specified, sessionId),
            _ => throw new InvalidOperationException("All sessions requires a peek or all-session clear operation.")
        };
}

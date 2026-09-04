namespace FullScreenManager;

internal enum SessionObservationAction
{
    Keep,
    Cleanup
}

internal readonly record struct WindowObservation(
    bool Exists,
    bool Visible,
    bool Iconic,
    bool Fullscreen,
    bool ClearlyWindowed,
    bool CurrentDesktop,
    bool Foreground,
    bool AwaitingReactivation,
    bool ProcessAlive,
    bool SharedWindowHost,
    int MissingObservations);

internal static class StatePolicy
{
    internal const int MissingConfirmationCount = 3;

    internal static bool ShouldDiscover(bool fullscreen, bool managed, bool retryReady) =>
        fullscreen && !managed && retryReady;

    internal static bool ShouldFollowEvacuatedWindow(bool wasForeground, bool sourceDesktopWasCurrent) =>
        wasForeground && sourceDesktopWasCurrent;

    internal static SessionObservationAction Decide(WindowObservation value)
    {
        if (!value.Exists)
        {
            if (!value.ProcessAlive) return SessionObservationAction.Cleanup;
            return value.MissingObservations >= MissingConfirmationCount &&
                   (value.SharedWindowHost || !value.CurrentDesktop)
                ? SessionObservationAction.Cleanup
                : SessionObservationAction.Keep;
        }

        if (!value.Visible)
            return value.AwaitingReactivation && value.CurrentDesktop && value.ProcessAlive
                ? SessionObservationAction.Keep
                : SessionObservationAction.Cleanup;

        if (value.Iconic)
            return !value.AwaitingReactivation && value.CurrentDesktop
                ? SessionObservationAction.Cleanup
                : SessionObservationAction.Keep;

        if (value.Fullscreen) return SessionObservationAction.Keep;
        return value.CurrentDesktop && value.Foreground && !value.AwaitingReactivation && value.ClearlyWindowed
            ? SessionObservationAction.Cleanup
            : SessionObservationAction.Keep;
    }

}

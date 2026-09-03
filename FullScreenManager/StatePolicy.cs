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

    internal static bool ShouldDiscover(bool fullscreen, bool suppressed, bool managed,
        bool initialDiscovery, bool foreground, bool retryReady) =>
        fullscreen && !suppressed && !managed && retryReady && (initialDiscovery || foreground);

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

    internal static void RunSelfTest()
    {
        Assert(ShouldDiscover(true, false, false, true, false, true),
            "Startup discovery must include background fullscreen windows.");
        Assert(!ShouldDiscover(true, false, false, false, false, true),
            "Runtime discovery must wait for a background window to become foreground.");
        Assert(ShouldDiscover(true, false, false, false, true, true),
            "A previously missed fullscreen window must be discovered when it becomes foreground.");
        Assert(!ShouldDiscover(true, true, false, true, true, true),
            "Suppressed auxiliary windows must not create sessions.");

        Assert(ShouldFollowEvacuatedWindow(true, true),
            "A foreground window launched on the active game Space must follow the user to its origin.");
        Assert(!ShouldFollowEvacuatedWindow(false, true),
            "A background window must be evacuated without stealing the user from the game.");
        Assert(!ShouldFollowEvacuatedWindow(true, false),
            "A window on an inactive Space must not switch the user's desktop.");

        Assert(Decide(Observation(iconic: true, current: true)) == SessionObservationAction.Cleanup,
            "A deliberate minimize on the dedicated Space must clean it up.");
        Assert(Decide(Observation(iconic: true, current: true, awaiting: true)) == SessionObservationAction.Keep,
            "An exclusive-mode reactivation must tolerate a temporary minimize.");
        Assert(Decide(Observation(iconic: true, current: false)) == SessionObservationAction.Keep,
            "Switching away from an exclusive game must not remove its Space.");
        Assert(Decide(Observation(fullscreen: false, clearlyWindowed: true, current: true, foreground: true)) ==
               SessionObservationAction.Cleanup,
            "A confirmed fullscreen exit must clean up the Space.");
        Assert(Decide(Observation(exists: false, processAlive: false)) == SessionObservationAction.Cleanup,
            "A dead process must clean up immediately.");
        Assert(Decide(Observation(exists: false, sharedHost: true,
                   missing: MissingConfirmationCount)) == SessionObservationAction.Cleanup,
            "A shared host without a replacement window must not retain a Space.");
        Assert(Decide(Observation(exists: false, current: true,
                   missing: MissingConfirmationCount)) == SessionObservationAction.Keep,
            "A live game recreating its HWND on the current Space must be tolerated.");
    }

    private static WindowObservation Observation(bool exists = true, bool visible = true,
        bool iconic = false, bool fullscreen = true, bool clearlyWindowed = false,
        bool current = false, bool foreground = false, bool awaiting = false,
        bool processAlive = true, bool sharedHost = false, int missing = 0) =>
        new(exists, visible, iconic, fullscreen, clearlyWindowed, current, foreground,
            awaiting, processAlive, sharedHost, missing);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

namespace FullScreenManager;

internal static class SessionRecovery
{
    internal static IReadOnlyList<ManagedSession> Recover(
        DesktopService desktops, SessionStore sessionStore, ManagedDesktopStore desktopStore)
    {
        var loaded = sessionStore.Load().OrderByDescending(session => session.UpdatedUtc).ToList();
        foreach (var session in loaded)
            desktopStore.Track(session.DedicatedDesktopId, session.OriginDesktopId);

        var selected = SelectUniqueSessions(loaded);
        var managedIds = loaded.Select(session => session.DedicatedDesktopId).ToHashSet();
        var safeOrigin = desktops.GetAll().FirstOrDefault(desktop => !managedIds.Contains(desktop.Id));

        foreach (var session in selected)
            RestoreSession(desktops, session, safeOrigin, managedIds);
        return selected;
    }

    private static IReadOnlyList<ManagedSession> SelectUniqueSessions(IEnumerable<ManagedSession> sessions)
    {
        var handles = new HashSet<long>();
        var desktops = new HashSet<Guid>();
        var result = new List<ManagedSession>();
        foreach (var session in sessions)
        {
            if (!handles.Add(session.WindowHandle) || !desktops.Add(session.DedicatedDesktopId))
            {
                AppLogger.Warning($"Отброшена дублирующая сохранённая сессия HWND {session.Hwnd}, Space {session.DedicatedDesktopId}");
                continue;
            }
            result.Add(session);
        }
        return result;
    }

    private static void RestoreSession(DesktopService desktops, ManagedSession session,
        DesktopService.Desktop? safeOrigin, IReadOnlySet<Guid> managedIds)
    {
        session.Dedicated = desktops.Find(session.DedicatedDesktopId);
        var storedOrigin = desktops.Find(session.OriginDesktopId);
        session.Origin = storedOrigin is not null &&
                         storedOrigin.Id != session.DedicatedDesktopId &&
                         !managedIds.Contains(storedOrigin.Id)
            ? storedOrigin
            : safeOrigin;
        if (session.Origin is not null) session.OriginDesktopId = session.Origin.Id;

        var ownerDesktop = desktops.GetWindowDesktop(session.Hwnd);
        var ownerOnSpace = session.Dedicated is not null &&
                           WindowInspector.IsSessionOwnerWindow(session.Hwnd, session) &&
                           (ownerDesktop is null || ownerDesktop.Id == session.Dedicated.Id);
        if (ownerOnSpace && session.ProcessStartedUtc is null)
            session.ProcessStartedUtc = WindowInspector.GetProcessStartedUtc(session.ProcessId);
        var fullscreen = ownerOnSpace && WindowInspector.IsFullscreen(session.Hwnd);
        var recoverableExclusiveWindow = ownerOnSpace && NativeMethods.IsIconic(session.Hwnd) &&
                                         !desktops.IsCurrent(session.Dedicated!);
        session.State = fullscreen || recoverableExclusiveWindow
            ? SessionState.Active
            : SessionState.RetryRequired;
        session.NextRetryUtc = DateTime.UtcNow;
        session.AwaitingFullscreenReactivation = recoverableExclusiveWindow;
        session.ActivationRequested = false;
        session.MissingDesktopObservations = 0;
        session.MissingWindowObservations = 0;
        session.UpdatedUtc = DateTime.UtcNow;
    }
}

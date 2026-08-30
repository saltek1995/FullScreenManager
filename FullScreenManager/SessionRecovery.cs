namespace FullScreenManager;

internal static class SessionRecovery
{
    internal static IReadOnlyList<ManagedSession> Recover(
        DesktopService desktops, SessionStore sessionStore, ManagedDesktopStore desktopStore)
    {
        var sessions = sessionStore.Load();
        foreach (var session in sessions)
            desktopStore.Track(session.DedicatedDesktopId, session.OriginDesktopId);
        CleanupOrphanedDesktops(desktops, desktopStore, sessions);

        var managedIds = sessions.Select(item => item.DedicatedDesktopId).ToHashSet();
        var sessionsByDesktop = sessions.GroupBy(item => item.DedicatedDesktopId)
            .ToDictionary(group => group.Key, group => group.First());
        var safeOrigin = desktops.GetAll().FirstOrDefault(desktop => !managedIds.Contains(desktop.Id));
        if (sessions.Count > 0 && safeOrigin is null) safeOrigin = CreateRescueDesktop(desktops);

        foreach (var session in sessions)
            RestoreSession(desktops, session, sessions, sessionsByDesktop, safeOrigin);
        return sessions;
    }

    internal static DesktopService.Desktop ResolveOrigin(
        DesktopService desktops, ManagedSession session, DesktopService.Desktop dedicated)
    {
        var origin = desktops.Find(session.OriginDesktopId);
        if (origin is not null && origin.Id != dedicated.Id) return origin;
        var current = desktops.Current();
        if (current.Id != dedicated.Id) return current;
        AppLogger.Warning($"Исходный стол сессии {dedicated.Id} исчез; создан безопасный стол возврата");
        return CreateRescueDesktop(desktops);
    }

    private static void RestoreSession(DesktopService desktops, ManagedSession session,
        IReadOnlyList<ManagedSession> allSessions,
        IReadOnlyDictionary<Guid, ManagedSession> sessionsByDesktop,
        DesktopService.Desktop? safeOrigin)
    {
        var dedicated = desktops.Find(session.DedicatedDesktopId);
        if (dedicated is null)
        {
            AppLogger.Warning($"Рабочий стол сессии {session.DedicatedDesktopId} уже отсутствует");
            return;
        }

        session.Dedicated = dedicated;
        var storedOrigin = desktops.Find(session.OriginDesktopId);
        session.Origin = IsValidOrigin(session, dedicated, storedOrigin, sessionsByDesktop)
            ? storedOrigin
            : safeOrigin ?? ResolveOrigin(desktops, session, dedicated);
        session.OriginDesktopId = session.Origin!.Id;
        var ownerIntact = WindowInspector.IsSessionOwnerWindow(session.Hwnd, session) &&
                          WindowInspector.IsFullscreen(session.Hwnd);
        session.State = session.State == SessionState.Active && ownerIntact
            ? SessionState.Active
            : SessionState.RetryRequired;
        var parent = allSessions.FirstOrDefault(item => item.DedicatedDesktopId == session.OriginDesktopId);
        if (parent is not null && WindowInspector.IsSameApplication(session, parent))
            session.State = SessionState.RetryRequired;
        if (session.State == SessionState.RetryRequired) session.NextRetryUtc = DateTime.UtcNow;
        session.UpdatedUtc = DateTime.UtcNow;
    }

    private static bool IsValidOrigin(ManagedSession session, DesktopService.Desktop dedicated,
        DesktopService.Desktop? origin, IReadOnlyDictionary<Guid, ManagedSession> sessionsByDesktop) =>
        origin is not null && origin.Id != dedicated.Id && IsAcyclicOrigin(session, origin.Id, sessionsByDesktop);

    private static bool IsAcyclicOrigin(ManagedSession session, Guid originId,
        IReadOnlyDictionary<Guid, ManagedSession> sessionsByDesktop)
    {
        var visited = new HashSet<Guid>();
        var current = originId;
        while (sessionsByDesktop.TryGetValue(current, out var parent))
        {
            if (current == session.DedicatedDesktopId || !visited.Add(current)) return false;
            current = parent.OriginDesktopId;
        }
        return current != session.DedicatedDesktopId;
    }

    private static void CleanupOrphanedDesktops(DesktopService desktops, ManagedDesktopStore store,
        IReadOnlyList<ManagedSession> sessions)
    {
        var referenced = sessions.Select(session => session.DedicatedDesktopId).ToHashSet();
        var records = store.Records;
        var trackedIds = records.Select(record => record.DesktopId).ToHashSet();
        var safeFallback = desktops.GetAll().FirstOrDefault(desktop => !trackedIds.Contains(desktop.Id));
        if (safeFallback is null && records.Any(record => !referenced.Contains(record.DesktopId)))
            safeFallback = CreateRescueDesktop(desktops);

        foreach (var record in records.Where(record => !referenced.Contains(record.DesktopId)))
            CleanupOrphan(desktops, store, record, trackedIds, safeFallback);
    }

    private static void CleanupOrphan(DesktopService desktops, ManagedDesktopStore store,
        ManagedDesktopRecord record, HashSet<Guid> trackedIds, DesktopService.Desktop? safeFallback)
    {
        var desktop = desktops.Find(record.DesktopId);
        if (desktop is null) { store.Forget(record.DesktopId); return; }
        var fallback = desktops.Find(record.FallbackId);
        if (fallback is null || fallback.Id == desktop.Id || trackedIds.Contains(fallback.Id)) fallback = safeFallback;
        if (fallback is null || fallback.Id == desktop.Id)
        {
            AppLogger.Warning($"Для осиротевшего Space {desktop.Id} не найден безопасный стол возврата");
            return;
        }
        try
        {
            if (desktops.IsCurrent(desktop)) desktops.Switch(fallback);
            desktops.Remove(desktop, fallback);
            if (desktops.Find(desktop.Id) is not null) return;
            store.Forget(desktop.Id);
            AppLogger.Info($"Удалён осиротевший Space {desktop.Id}");
        }
        catch (Exception ex) { AppLogger.Error($"Не удалось удалить осиротевший Space {desktop.Id}", ex); }
    }

    private static DesktopService.Desktop CreateRescueDesktop(DesktopService desktops)
    {
        var desktop = desktops.Create();
        try { desktops.SetName(desktop, "Рабочий стол"); }
        catch (Exception ex) { AppLogger.Warning($"Не удалось назвать резервный стол: {ex.Message}"); }
        return desktop;
    }
}

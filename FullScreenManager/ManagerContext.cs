using System.Diagnostics;
using System.Runtime.InteropServices;
using static FullScreenManager.WindowInspector;
using Timer = System.Windows.Forms.Timer;

namespace FullScreenManager;

internal sealed class ManagerContext : ApplicationContext
{
    private readonly Timer _timer = new() { Interval = 100 };
    private DesktopService _desktops = new();
    private readonly Dictionary<IntPtr, bool> _previous = [];
    private readonly Dictionary<IntPtr, ManagedSession> _sessions = [];
    private readonly SessionStore _sessionStore = new();
    private readonly ManagedDesktopStore _desktopStore = new();
    private readonly Queue<StartupWindow> _startupWindows = new();
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _enabledItem;
    private bool _busy;
    private bool _enabled = true;

    public ManagerContext()
    {
        RecoverSessions();
        SnapshotWindows();

        (_tray, _enabledItem) = TrayUi.Create(ExitThread);
        _enabledItem.CheckedChanged += (_, _) => _enabled = _enabledItem.Checked;

        _timer.Tick += Tick;
        _timer.Start();
    }

    private void Tick(object? sender, EventArgs args)
    {
        try { TickCore(); }
        catch (Exception ex)
        {
            AppLogger.Error("Сбой цикла мониторинга; COM-подключение будет восстановлено", ex);
            ReconnectDesktopService();
        }
    }

    private void ReconnectDesktopService()
    {
        try
        {
            _desktops = new DesktopService();
            foreach (var session in _sessions.Values.Distinct())
            {
                session.Dedicated = _desktops.Find(session.DedicatedDesktopId);
                session.Origin = _desktops.Find(session.OriginDesktopId);
            }
        }
        catch (Exception ex) { AppLogger.Error("Explorer пока не готов принять новое COM-подключение", ex); }
    }

    private void TickCore()
    {
        if (_busy) return;

        MonitorSessions();
        if (_busy || !_enabled) return;

        if (_startupWindows.Count > 0)
        {
            ProcessStartupWindows();
            return;
        }

        var alive = ScanWindows();
        foreach (var hwnd in _previous.Keys.Where(hwnd => !alive.Contains(hwnd)).ToList())
            _previous.Remove(hwnd);
    }

    private HashSet<IntPtr> ScanWindows()
    {
        var alive = new HashSet<IntPtr>();
        Enumerate(hwnd => { alive.Add(hwnd); InspectWindow(hwnd); });
        return alive;
    }

    private void InspectWindow(IntPtr hwnd)
    {
        var fullscreen = IsFullscreen(hwnd);
        _previous.TryGetValue(hwnd, out var wasFullscreen);
        if (!_sessions.ContainsKey(hwnd) && fullscreen && !wasFullscreen &&
            hwnd == NativeMethods.GetForegroundWindow()) HandleNewFullscreenWindow(hwnd);
        _previous[hwnd] = fullscreen;
    }

    private void HandleNewFullscreenWindow(IntPtr hwnd)
    {
        var desktop = _desktops.Current();
        var host = _sessions.Values.Distinct().FirstOrDefault(session => session.Dedicated?.Id == desktop.Id);
        if (host is null || !BelongsToApplication(hwnd, host)) SendToNewDesktop(hwnd, desktop);
        else AppLogger.Info($"Вспомогательное полноэкранное окно {hwnd} оставлено в Space {host.DedicatedDesktopId}");
    }

    private void MonitorSessions()
    {
        foreach (var pair in _sessions.ToList())
            MonitorSession(pair.Key, pair.Value);
    }

    private void MonitorSession(IntPtr hwnd, ManagedSession session)
    {
        var dedicated = _desktops.Find(session.DedicatedDesktopId);
        if (dedicated is null)
        {
            HandleMissingDesktop(session);
            return;
        }

        session.DesktopMissingSince = null;
        session.Dedicated = dedicated;
        if (!EnsureOrigin(session, dedicated)) return;

        if (session.State == SessionState.RetryRequired)
        {
            if (DateTime.UtcNow >= session.NextRetryUtc) CleanupSession(hwnd, session, false);
            return;
        }

        if (IsSessionOwnerWindow(hwnd, session)) MonitorOwnerWindow(hwnd, session);
        else MonitorReplacementWindow(hwnd, session);
    }

    private void HandleMissingDesktop(ManagedSession session)
    {
        if (session.State is SessionState.Removing or SessionState.RetryRequired)
        {
            ForgetSession(session);
            return;
        }

        session.DesktopMissingSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - session.DesktopMissingSince.Value < TimeSpan.FromSeconds(2)) return;
        AppLogger.Warning($"Созданный стол {session.DedicatedDesktopId} был удалён вне приложения");
        ForgetSession(session);
    }

    private bool EnsureOrigin(ManagedSession session, DesktopService.Desktop dedicated)
    {
        var origin = _desktops.Find(session.OriginDesktopId);
        if (origin is not null && origin.Id != dedicated.Id)
        {
            session.OriginMissingSince = null;
            session.Origin = origin;
            return true;
        }

        session.OriginMissingSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - session.OriginMissingSince.Value < TimeSpan.FromSeconds(2)) return false;
        try
        {
            session.Origin = SessionRecovery.ResolveOrigin(_desktops, session, dedicated);
            session.OriginDesktopId = session.Origin.Id;
            session.OriginMissingSince = null;
            SaveSessions();
            return true;
        }
        catch (Exception ex)
        {
            ScheduleRetry(session, ex);
            return false;
        }
    }

    private void MonitorOwnerWindow(IntPtr hwnd, ManagedSession session)
    {
        if (!NativeMethods.IsWindowVisible(hwnd) || session.Dedicated is null)
        {
            AppLogger.Info($"Сессия {session.DedicatedDesktopId}: окно скрыто в трей");
            CleanupSession(hwnd, session, false);
            return;
        }

        WindowMover.Reconcile(_desktops, session);
        UpdateDesktopName(hwnd, session);
        var isCurrent = _desktops.IsCurrent(session.Dedicated);
        var isForeground = hwnd == NativeMethods.GetForegroundWindow();
        var wasCurrent = session.WasOnDedicatedDesktop;
        var wasForeground = session.WasForeground;
        session.WasOnDedicatedDesktop = isCurrent;
        session.WasForeground = isForeground;
        if ((isCurrent && !wasCurrent) || (isForeground && !wasForeground))
            BeginDisplayModeTransition(session);

        if (NativeMethods.IsIconic(hwnd))
        {
            // A deliberate minimize starts while the game is foreground on its
            // current Space. Minimize/restore caused by an exclusive mode switch
            // must not remove the dedicated desktop.
            if (session.AwaitingFullscreenReactivation && isCurrent)
            {
                // Activation is requested once when this Space is selected.
                // Reasserting foreground from the monitor loop makes a foreign
                // window and the game steal focus from each other every tick.
                return;
            }
            else if (isCurrent && wasCurrent && wasForeground)
            {
                AppLogger.Info($"Сессия {session.DedicatedDesktopId}: окно свёрнуто");
                ReturnWindow(hwnd, session, true);
            }
            return;
        }

        MonitorFullscreenState(hwnd, session, isCurrent, isForeground);
    }

    private void MonitorFullscreenState(IntPtr hwnd, ManagedSession session, bool isCurrent, bool isForeground)
    {
        if (IsFullscreen(hwnd))
        {
            session.AwaitingFullscreenReactivation = false;
            return;
        }
        if (!isForeground || !isCurrent || session.AwaitingFullscreenReactivation ||
            !WindowInspector.IsClearlyWindowed(hwnd)) return;
        AppLogger.Info($"Сессия {session.DedicatedDesktopId}: подтверждён выход из полноэкранного режима");
        ReturnWindow(hwnd, session, false);
    }

    private void MonitorReplacementWindow(IntPtr hwnd, ManagedSession session)
    {
        var replacement = FindReplacementWindow(session);
        if (replacement == IntPtr.Zero)
        {
            if (IsProcessAlive(session.ProcessId)) return;
            AppLogger.Info($"Сессия {session.DedicatedDesktopId}: окно закрыто");
            CleanupSession(hwnd, session, false);
            return;
        }

        _sessions.Remove(hwnd);
        session.WindowHandle = replacement.ToInt64();
        session.UpdatedUtc = DateTime.UtcNow;
        session.WasForeground = false;
        session.WasOnDedicatedDesktop = false;
        BeginDisplayModeTransition(session);
        _sessions[replacement] = session;
        _previous[replacement] = IsFullscreen(replacement);
        UpdateDesktopName(replacement, session);
        SaveSessions();
    }

    private IntPtr FindReplacementWindow(ManagedSession session)
    {
        var fullscreenMatches = new List<IntPtr>();
        var visibleMatches = new List<IntPtr>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != session.ProcessId || !NativeMethods.IsWindowVisible(hwnd)) return true;
            visibleMatches.Add(hwnd);
            if (IsFullscreen(hwnd)) fullscreenMatches.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        if (fullscreenMatches.Count == 1) return fullscreenMatches[0];
        return session.AwaitingFullscreenReactivation && visibleMatches.Count == 1
            ? visibleMatches[0]
            : IntPtr.Zero;
    }

    private void ProcessStartupWindows()
    {
        _busy = true;
        _timer.Stop();
        var foreground = NativeMethods.GetForegroundWindow();
        DesktopService.Desktop? desktopToShow = null;
        var windowToShow = IntPtr.Zero;

        try
        {
            while (_startupWindows.Count > 0)
            {
                var startup = _startupWindows.Dequeue();
                if (!NativeMethods.IsWindow(startup.Handle) || !IsFullscreen(startup.Handle)) continue;
                var created = ProcessStartupWindow(startup);
                if (created is not null && (startup.Handle == foreground || desktopToShow is null))
                {
                    desktopToShow = created;
                    windowToShow = startup.Handle;
                }
            }

            if (desktopToShow is not null)
            {
                _desktops.Switch(desktopToShow);
                if (_sessions.TryGetValue(windowToShow, out var session)) ActivateFullscreenWindow(windowToShow, session);
            }
        }
        finally
        {
            _busy = false;
            _timer.Start();
        }
    }

    private DesktopService.Desktop? ProcessStartupWindow(StartupWindow startup)
    {
        if (IsAlreadyManaged(startup.Handle)) return null;
        DesktopService.Desktop? dedicated = null;
        ManagedSession? managed = null;
        try
        {
            dedicated = _desktops.Create();
            _desktopStore.Track(dedicated.Id, startup.Origin.Id);
            _desktops.MoveAfterPrimary(dedicated);
            var name = GetApplicationName(startup.Handle);
            NativeMethods.GetWindowThreadProcessId(startup.Handle, out var pid);
            managed = CreateSession(startup.Handle, pid, startup.Origin, dedicated, name);
            try { _desktops.SetName(dedicated, name); } catch (Exception ex) { AppLogger.Warning(ex.Message); }
            _desktops.MoveWindow(startup.Handle, dedicated);
            managed.State = SessionState.Active;
            SaveSessions();
            return dedicated;
        }
        catch (COMException ex) when ((uint)ex.HResult == 0x8002802B)
        {
            RollbackCreatedSession(managed, dedicated, startup.Origin);
            return null;
        }
        catch (Exception ex)
        {
            RollbackCreatedSession(managed, dedicated, startup.Origin);
            ShowError("Не удалось обработать уже открытое полноэкранное окно", ex);
            return null;
        }
    }

    private void SendToNewDesktop(IntPtr hwnd, DesktopService.Desktop? knownOrigin)
    {
        if (IsAlreadyManaged(hwnd)) return;
        _busy = true;
        _timer.Stop();
        DesktopService.Desktop? dedicated = null;
        ManagedSession? managed = null;
        try
        {
            // A fullscreen window opened from another managed Space must return
            // to that Space, just like a nested fullscreen view on macOS.
            var origin = knownOrigin ?? _desktops.Current();
            dedicated = _desktops.Create();
            _desktopStore.Track(dedicated.Id, origin.Id);
            _desktops.MoveAfterPrimary(dedicated);
            var desktopName = GetApplicationName(hwnd);
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            managed = CreateSession(hwnd, pid, origin, dedicated, desktopName);
            try { _desktops.SetName(dedicated, desktopName); } catch { }
            _desktops.MoveWindow(hwnd, dedicated);
            managed.State = SessionState.Active;
            SaveSessions();
            _desktops.Switch(dedicated);
            ActivateFullscreenWindow(hwnd, managed);
        }
        catch (Exception ex)
        {
            RollbackCreatedSession(managed, dedicated, managed?.Origin ?? _desktops.Current());
            ShowError("Не удалось создать полноэкранный рабочий стол", ex);
        }
        finally
        {
            _busy = false;
            _timer.Start();
        }
    }

    private void ReturnWindow(IntPtr hwnd, ManagedSession session, bool minimized)
    {
        _busy = true;
        _timer.Stop();
        try
        {
            if (session.Origin is null || session.Dedicated is null)
                throw new InvalidOperationException("Сессия потеряла ссылки на рабочие столы.");
            session.State = SessionState.Returning;
            session.UpdatedUtc = DateTime.UtcNow;
            SaveSessions();
            _desktops.MoveWindow(hwnd, session.Origin);
            WindowMover.MoveAllToOrigin(_desktops, session);
            ReparentChildSessions(session);
            var wasOnDedicated = _desktops.IsCurrent(session.Dedicated);
            if (wasOnDedicated) _desktops.Switch(session.Origin);
            session.State = SessionState.Removing;
            SaveSessions();
            _desktops.Remove(session.Dedicated, session.Origin);
            MarkRemovalPending(session);
            if (!minimized)
            {
                NativeMethods.SetForegroundWindow(hwnd);
            }
            _previous[hwnd] = false;
        }
        catch (Exception ex)
        {
            ScheduleRetry(session, ex);
            ShowError("Не удалось вернуть окно на исходный рабочий стол", ex);
        }
        finally
        {
            _busy = false;
            _timer.Start();
        }
    }

    private void CleanupSession(IntPtr hwnd, ManagedSession session, bool moveWindow)
    {
        _busy = true;
        _timer.Stop();
        try
        {
            if (session.Origin is null || session.Dedicated is null)
                throw new InvalidOperationException("Не удалось восстановить рабочие столы сессии.");
            session.State = SessionState.Removing;
            SaveSessions();
            if (moveWindow && NativeMethods.IsWindow(hwnd)) _desktops.MoveWindow(hwnd, session.Origin);
            WindowMover.MoveAllToOrigin(_desktops, session);
            ReparentChildSessions(session);
            if (_desktops.IsCurrent(session.Dedicated)) _desktops.Switch(session.Origin);
            _desktops.Remove(session.Dedicated, session.Origin);
            MarkRemovalPending(session);
        }
        catch (Exception ex) { ScheduleRetry(session, ex); }
        finally
        {
            _busy = false;
            _timer.Start();
        }
    }

    private ManagedSession CreateSession(IntPtr hwnd, uint processId, DesktopService.Desktop origin,
        DesktopService.Desktop dedicated, string name)
    {
        if (IsAlreadyManaged(hwnd))
            throw new InvalidOperationException($"HWND {hwnd} уже принадлежит активной сессии.");
        if (_sessions.Values.Any(existing => existing.DedicatedDesktopId == dedicated.Id))
            throw new InvalidOperationException($"Space {dedicated.Id} уже принадлежит активной сессии.");

        var session = new ManagedSession
        {
            WindowHandle = hwnd.ToInt64(),
            ProcessId = processId,
            ExecutablePath = GetExecutablePath(processId),
            OriginDesktopId = origin.Id,
            DedicatedDesktopId = dedicated.Id,
            Origin = origin,
            Dedicated = dedicated,
            Name = name,
            State = SessionState.Creating,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        BeginDisplayModeTransition(session);
        if (!_sessions.TryAdd(hwnd, session))
            throw new InvalidOperationException($"Не удалось зарегистрировать HWND {hwnd}.");
        _desktopStore.Track(dedicated.Id, origin.Id);
        SaveSessions();
        AppLogger.Info($"Создана сессия {dedicated.Id} для HWND {hwnd} ({name})");
        return session;
    }

    private void RecoverSessions()
    {
        foreach (var session in SessionRecovery.Recover(_desktops, _sessionStore, _desktopStore))
        {
            if (IsAlreadyManaged(session.Hwnd) ||
                _sessions.Values.Any(existing => existing.DedicatedDesktopId == session.DedicatedDesktopId))
            {
                AppLogger.Warning($"Пропущена дублирующая восстановленная сессия HWND {session.Hwnd}, Space {session.DedicatedDesktopId}");
                continue;
            }
            BeginDisplayModeTransition(session);
            _sessions.Add(session.Hwnd, session);
        }
        SaveSessions();
    }

    private static void BeginDisplayModeTransition(ManagedSession session) =>
        session.AwaitingFullscreenReactivation = true;

    private static bool IsProcessAlive(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return !process.HasExited;
        }
        catch { return false; }
    }

    private static void ActivateFullscreenWindow(IntPtr hwnd, ManagedSession session)
    {
        BeginDisplayModeTransition(session);
        if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindowAsync(hwnd, 9); // SW_RESTORE
        NativeMethods.SetForegroundWindow(hwnd);
    }

    private void SaveSessions() => _sessionStore.Save(_sessions.Values.Distinct());

    private void MarkRemovalPending(ManagedSession session)
    {
        session.State = SessionState.RetryRequired;
        session.NextRetryUtc = DateTime.UtcNow.AddMilliseconds(500);
        session.UpdatedUtc = DateTime.UtcNow;
        SaveSessions();
    }

    private void ForgetSession(ManagedSession session)
    {
        foreach (var key in _sessions.Where(pair => ReferenceEquals(pair.Value, session))
                     .Select(pair => pair.Key).ToList())
            _sessions.Remove(key);
        // Do not immediately rediscover the same still-fullscreen transient
        // window after its session has just been removed.
        _previous[session.Hwnd] = NativeMethods.IsWindow(session.Hwnd) && IsFullscreen(session.Hwnd);
        _desktopStore.Forget(session.DedicatedDesktopId);
        SaveSessions();
    }

    private void RollbackCreatedSession(ManagedSession? session, DesktopService.Desktop? dedicated,
        DesktopService.Desktop fallback)
    {
        if (dedicated is null) return;
        try
        {
            if (session is not null) WindowMover.MoveAllToOrigin(_desktops, session);
            if (_desktops.IsCurrent(dedicated)) _desktops.Switch(fallback);
            _desktops.Remove(dedicated, fallback);
            if (session is not null) MarkRemovalPending(session);
            else if (_desktops.Find(dedicated.Id) is null) _desktopStore.Forget(dedicated.Id);
        }
        catch (Exception cleanupError)
        {
            if (session is not null) ScheduleRetry(session, cleanupError);
            else AppLogger.Error($"Не удалось откатить незарегистрированный стол {dedicated.Id}", cleanupError);
        }
    }

    private void ReparentChildSessions(ManagedSession removedParent)
    {
        if (removedParent.Origin is null) return;
        foreach (var child in _sessions.Values.Distinct().Where(child =>
                     !ReferenceEquals(child, removedParent) &&
                     child.OriginDesktopId == removedParent.DedicatedDesktopId))
        {
            child.Origin = removedParent.Origin;
            child.OriginDesktopId = removedParent.Origin.Id;
            child.UpdatedUtc = DateTime.UtcNow;
        }
        SaveSessions();
    }

    private void ScheduleRetry(ManagedSession session, Exception exception)
    {
        session.State = SessionState.RetryRequired;
        session.RetryCount++;
        session.NextRetryUtc = DateTime.UtcNow.AddMilliseconds(
            Math.Min(30_000, 500 * Math.Pow(2, Math.Min(session.RetryCount, 6))));
        session.UpdatedUtc = DateTime.UtcNow;
        SaveSessions();
        AppLogger.Error($"Операция с сессией {session.DedicatedDesktopId} будет повторена", exception);
    }

    private void ShowError(string message, Exception exception) =>
        _tray.ShowBalloonTip(7000, "FullScreenManager", $"{message}: {exception.Message}", ToolTipIcon.Error);

    private void SnapshotWindows()
    {
        var origin = _desktops.Current();
        Enumerate(hwnd =>
        {
            var zoomed = IsFullscreen(hwnd);
            _previous[hwnd] = zoomed;
            if (zoomed && !_sessions.ContainsKey(hwnd)) _startupWindows.Enqueue(new StartupWindow(hwnd, origin));
        });
    }

    private bool IsAlreadyManaged(IntPtr hwnd) =>
        _sessions.ContainsKey(hwnd) || _sessions.Values.Any(session => session.Hwnd == hwnd);

    private void UpdateDesktopName(IntPtr hwnd, ManagedSession session)
    {
        var currentName = GetApplicationName(hwnd);
        var nameChanged = !string.Equals(currentName, session.Name, StringComparison.Ordinal);
        if (!nameChanged && DateTime.UtcNow < session.NextNameSyncUtc) return;
        try
        {
            if (session.Dedicated is null) return;
            _desktops.SetName(session.Dedicated, currentName);
            session.Name = currentName;
            session.NextNameSyncUtc = DateTime.UtcNow.AddSeconds(5);
            session.UpdatedUtc = DateTime.UtcNow;
            SaveSessions();
        }
        catch (Exception ex)
        {
            session.NextNameSyncUtc = DateTime.UtcNow.AddSeconds(5);
            AppLogger.Error($"Не удалось синхронизировать имя стола {session.DedicatedDesktopId}", ex);
        }
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        foreach (var pair in _sessions.ToList())
        {
            var hwnd = pair.Key;
            var session = pair.Value;
            try
            {
                if (session.Origin is null || session.Dedicated is null) continue;
                if (NativeMethods.IsWindow(hwnd)) _desktops.MoveWindow(hwnd, session.Origin);
                WindowMover.MoveAllToOrigin(_desktops, session);
                ReparentChildSessions(session);
                if (_desktops.IsCurrent(session.Dedicated)) _desktops.Switch(session.Origin);
                _desktops.Remove(session.Dedicated, session.Origin);
            }
            catch { }
        }
        SaveSessions();
        _tray.Visible = false;
        _tray.Dispose();
        base.ExitThreadCore();
    }

    private sealed record StartupWindow(IntPtr Handle, DesktopService.Desktop Origin);
}

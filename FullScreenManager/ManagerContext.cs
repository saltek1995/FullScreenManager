using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

namespace FullScreenManager;

internal sealed class ManagerContext : ApplicationContext
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "FullScreenManager";
    private const string ScheduledTaskName = "FullScreenManager";

    private readonly Timer _timer = new() { Interval = 100 };
    private DesktopService _desktops = new();
    private readonly Dictionary<IntPtr, bool> _previous = [];
    private readonly Dictionary<IntPtr, ManagedSession> _sessions = [];
    private readonly SessionStore _sessionStore = new();
    private readonly ManagedDesktopStore _desktopStore = new();
    private readonly Queue<StartupWindow> _startupWindows = new();
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _autostartItem;
    private bool _busy;
    private bool _enabled = true;

    public ManagerContext()
    {
        RecoverSessions();
        SnapshotWindows();

        _enabledItem = new ToolStripMenuItem("Включено") { Checked = true, CheckOnClick = true };
        _enabledItem.CheckedChanged += (_, _) => _enabled = _enabledItem.Checked;

        _autostartItem = new ToolStripMenuItem("Запускать вместе с Windows")
        {
            Checked = IsAutostartEnabled()
        };
        _autostartItem.Click += (_, _) => SetAutostart(!_autostartItem.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_enabledItem);
        menu.Items.Add(_autostartItem);
        menu.Items.Add("Настроить окна всех рабочих столов…", null,
            (_, _) => OpenDesktopMultitaskingSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("О программе", null, (_, _) => ShowAbout());
        menu.Items.Add("Выход", null, (_, _) => ExitThread());

        var executable = Environment.ProcessPath!;
        var icon = Icon.ExtractAssociatedIcon(executable) ?? SystemIcons.Application;
        _tray = new NotifyIcon
        {
            Text = "FullScreenManager — включено",
            Icon = icon,
            ContextMenuStrip = menu,
            Visible = true
        };
        _enabledItem.CheckedChanged += (_, _) =>
            _tray.Text = _enabled ? "FullScreenManager — включено" : "FullScreenManager — приостановлено";

        _timer.Tick += Tick;
        _timer.Start();
    }

    private void Tick(object? sender, EventArgs args)
    {
        try { TickCore(); }
        catch (Exception ex)
        {
            AppLogger.Error("Сбой цикла мониторинга; COM-подключение будет восстановлено", ex);
            try
            {
                _desktops = new DesktopService();
                foreach (var session in _sessions.Values.Distinct())
                {
                    session.Dedicated = _desktops.Find(session.DedicatedDesktopId);
                    session.Origin = _desktops.Find(session.OriginDesktopId);
                }
            }
            catch (Exception reconnectError)
            {
                AppLogger.Error("Explorer пока не готов принять новое COM-подключение", reconnectError);
            }
        }
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

        var alive = new HashSet<IntPtr>();
        Enumerate(hwnd =>
        {
            alive.Add(hwnd);
            var zoomed = IsFullscreen(hwnd);
            _previous.TryGetValue(hwnd, out var wasZoomed);

            if (!_sessions.ContainsKey(hwnd) && zoomed && !wasZoomed && hwnd == NativeMethods.GetForegroundWindow())
            {
                var currentDesktop = _desktops.Current();
                var hostingSession = _sessions.Values.Distinct().FirstOrDefault(session =>
                    session.Dedicated?.Id == currentDesktop.Id);
                if (hostingSession is null || !BelongsToApplication(hwnd, hostingSession))
                    SendToNewDesktop(hwnd, currentDesktop);
                else
                    AppLogger.Info($"Вспомогательное полноэкранное окно {hwnd} оставлено в Space {hostingSession.DedicatedDesktopId}");
            }

            _previous[hwnd] = zoomed;
        });

        foreach (var hwnd in _previous.Keys.Where(hwnd => !alive.Contains(hwnd)).ToList())
        {
            _previous.Remove(hwnd);
        }
    }

    private void MonitorSessions()
    {
        foreach (var pair in _sessions.ToList())
        {
            var hwnd = pair.Key;
            var session = pair.Value;

            var liveDedicated = _desktops.Find(session.DedicatedDesktopId);
            if (liveDedicated is null)
            {
                if (session.State is SessionState.Removing or SessionState.RetryRequired)
                {
                    ForgetSession(session);
                    continue;
                }
                session.DesktopMissingSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - session.DesktopMissingSince.Value < TimeSpan.FromSeconds(2))
                    continue;
                AppLogger.Warning($"Созданный стол {session.DedicatedDesktopId} был удалён вне приложения");
                ForgetSession(session);
                continue;
            }
            session.DesktopMissingSince = null;
            session.Dedicated = liveDedicated;

            var liveOrigin = _desktops.Find(session.OriginDesktopId);
            if (liveOrigin is null || liveOrigin.Id == liveDedicated.Id)
            {
                session.OriginMissingSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - session.OriginMissingSince.Value < TimeSpan.FromSeconds(2))
                    continue;
                try
                {
                    session.Origin = ResolveRecoveryOrigin(session, liveDedicated);
                    session.OriginDesktopId = session.Origin.Id;
                    session.OriginMissingSince = null;
                    SaveSessions();
                }
                catch (Exception ex)
                {
                    ScheduleRetry(session, ex);
                    continue;
                }
            }
            else
            {
                session.OriginMissingSince = null;
                session.Origin = liveOrigin;
            }

            if (session.State == SessionState.RetryRequired)
            {
                if (DateTime.UtcNow >= session.NextRetryUtc)
                    CleanupSession(hwnd, session, false);
                continue;
            }

            if (IsSessionOwnerWindow(hwnd, session))
            {
                session.MissingSince = null;
                if (!NativeMethods.IsWindowVisible(hwnd))
                {
                    AppLogger.Info($"Сессия {session.DedicatedDesktopId}: окно скрыто в трей");
                    CleanupSession(hwnd, session, false);
                    continue;
                }
                if (session.Dedicated is null)
                {
                    CleanupSession(hwnd, session, false);
                    continue;
                }

                ReconcileDedicatedDesktop(session);
                UpdateDesktopName(hwnd, session);

                var minimized = NativeMethods.IsIconic(hwnd);
                if (minimized)
                {
                    AppLogger.Info($"Сессия {session.DedicatedDesktopId}: окно свёрнуто");
                    ReturnWindow(hwnd, session, true);
                    continue;
                }

                if (IsFullscreen(hwnd))
                {
                    session.FullscreenLostSince = null;
                    continue;
                }

                // Task View and desktop switch animations can change the reported
                // frame geometry of background/ApplicationFrameHost windows. Only
                // the active owner window can genuinely be restored by the user.
                if (hwnd != NativeMethods.GetForegroundWindow())
                {
                    session.FullscreenLostSince = null;
                    continue;
                }

                // Cloaked/background app windows can temporarily report a normal
                // rectangle while Windows is switching virtual desktops. A real
                // restore happens on the window's current Space and stays stable.
                if (!_desktops.IsCurrent(session.Dedicated))
                {
                    session.FullscreenLostSince = null;
                    continue;
                }

                session.FullscreenLostSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - session.FullscreenLostSince.Value >= TimeSpan.FromMilliseconds(300))
                {
                    AppLogger.Info($"Сессия {session.DedicatedDesktopId}: подтверждён выход из полноэкранного режима");
                    ReturnWindow(hwnd, session, false);
                }
                continue;
            }

            var replacement = FindReplacementWindow(session);
            if (replacement != IntPtr.Zero)
            {
                _sessions.Remove(hwnd);
                session.WindowHandle = replacement.ToInt64();
                session.UpdatedUtc = DateTime.UtcNow;
                _sessions[replacement] = session;
                _previous[replacement] = IsFullscreen(replacement);
                UpdateDesktopName(replacement, session);
                SaveSessions();
                continue;
            }

            AppLogger.Info($"Сессия {session.DedicatedDesktopId}: окно закрыто");
            CleanupSession(hwnd, session, false);
        }
    }

    private IntPtr FindReplacementWindow(ManagedSession session)
    {
        var matches = new List<IntPtr>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == session.ProcessId && NativeMethods.IsWindowVisible(hwnd) && IsFullscreen(hwnd))
                matches.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return matches.Count == 1 ? matches[0] : IntPtr.Zero;
    }

    private void ProcessStartupWindows()
    {
        _busy = true;
        _timer.Stop();
        var foreground = NativeMethods.GetForegroundWindow();
        DesktopService.Desktop? desktopToShow = null;

        try
        {
            while (_startupWindows.Count > 0)
            {
                var startup = _startupWindows.Dequeue();
                if (!NativeMethods.IsWindow(startup.Handle) || !IsFullscreen(startup.Handle)) continue;

                DesktopService.Desktop? dedicated = null;
                ManagedSession? managed = null;
                try
                {
                    dedicated = _desktops.Create();
                    _desktopStore.Track(dedicated.Id, startup.Origin.Id);
                    _desktops.MoveAfterPrimary(dedicated);
                    var desktopName = GetApplicationName(startup.Handle);
                    NativeMethods.GetWindowThreadProcessId(startup.Handle, out var pid);
                    managed = CreateSession(startup.Handle, pid, startup.Origin, dedicated, desktopName);
                    try { _desktops.SetName(dedicated, desktopName); } catch { }
                    _desktops.MoveWindow(startup.Handle, dedicated);
                    managed.State = SessionState.Active;
                    SaveSessions();
                    if (startup.Handle == foreground || desktopToShow is null) desktopToShow = dedicated;
                    dedicated = null;
                }
                catch (COMException ex) when ((uint)ex.HResult == 0x8002802B)
                {
                    // The application destroyed/recreated its HWND while startup
                    // inventory was running. The normal polling path will see the new HWND.
                    RollbackCreatedSession(managed, dedicated, startup.Origin);
                }
                catch (Exception ex)
                {
                    RollbackCreatedSession(managed, dedicated, startup.Origin);
                    ShowError("Не удалось обработать уже открытое полноэкранное окно", ex);
                }
            }

            if (desktopToShow is not null) _desktops.Switch(desktopToShow);
        }
        finally
        {
            _busy = false;
            _timer.Start();
        }
    }

    private void SendToNewDesktop(IntPtr hwnd, DesktopService.Desktop? knownOrigin)
    {
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
            NativeMethods.ShowWindow(hwnd, 3); // SW_MAXIMIZE
            _desktops.Switch(dedicated);
            NativeMethods.SetForegroundWindow(hwnd);
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
            MoveAllWindowsToOrigin(session);
            ReparentChildSessions(session);
            var wasOnDedicated = _desktops.IsCurrent(session.Dedicated);
            if (wasOnDedicated) _desktops.Switch(session.Origin);
            session.State = SessionState.Removing;
            SaveSessions();
            _desktops.Remove(session.Dedicated, session.Origin);
            MarkRemovalPending(session);
            if (!minimized)
            {
                NativeMethods.ShowWindow(hwnd, 9); // SW_RESTORE
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
            MoveAllWindowsToOrigin(session);
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
        _sessions[hwnd] = session;
        _desktopStore.Track(dedicated.Id, origin.Id);
        SaveSessions();
        AppLogger.Info($"Создана сессия {dedicated.Id} для HWND {hwnd} ({name})");
        return session;
    }

    private void RecoverSessions()
    {
        var storedSessions = _sessionStore.Load();
        foreach (var stored in storedSessions)
            _desktopStore.Track(stored.DedicatedDesktopId, stored.OriginDesktopId);
        CleanupOrphanedManagedDesktops(storedSessions);
        var managedDesktopIds = storedSessions.Select(item => item.DedicatedDesktopId).ToHashSet();
        var sessionsByDesktop = storedSessions
            .GroupBy(item => item.DedicatedDesktopId)
            .ToDictionary(group => group.Key, group => group.First());
        var safeOrigin = _desktops.GetAll().FirstOrDefault(desktop => !managedDesktopIds.Contains(desktop.Id));
        if (storedSessions.Count > 0 && safeOrigin is null)
        {
            safeOrigin = _desktops.Create();
            try { _desktops.SetName(safeOrigin, "Рабочий стол"); } catch { }
        }

        foreach (var session in storedSessions)
        {
            var dedicated = _desktops.Find(session.DedicatedDesktopId);
            if (dedicated is null)
            {
                AppLogger.Warning($"Рабочий стол сессии {session.DedicatedDesktopId} уже отсутствует");
                continue;
            }

            session.Dedicated = dedicated;
            var storedOrigin = _desktops.Find(session.OriginDesktopId);
            session.Origin = storedOrigin is not null && storedOrigin.Id != dedicated.Id &&
                             IsAcyclicOrigin(session, storedOrigin.Id, sessionsByDesktop)
                ? storedOrigin
                : safeOrigin ?? ResolveRecoveryOrigin(session, dedicated);
            session.OriginDesktopId = session.Origin.Id;
            var ownerIsIntact = IsSessionOwnerWindow(session.Hwnd, session) &&
                                IsFullscreen(session.Hwnd);
            session.State = session.State == SessionState.Active && ownerIsIntact
                ? SessionState.Active
                : SessionState.RetryRequired;
            var parentSession = storedSessions.FirstOrDefault(parent =>
                parent.DedicatedDesktopId == session.OriginDesktopId);
            if (parentSession is not null && IsSameApplication(session, parentSession))
                session.State = SessionState.RetryRequired;
            if (session.State == SessionState.RetryRequired)
                session.NextRetryUtc = DateTime.UtcNow;
            session.UpdatedUtc = DateTime.UtcNow;
            _sessions[session.Hwnd] = session;
        }
        SaveSessions();
    }

    private void CleanupOrphanedManagedDesktops(IReadOnlyList<ManagedSession> storedSessions)
    {
        var referenced = storedSessions.Select(session => session.DedicatedDesktopId).ToHashSet();
        var records = _desktopStore.Records;
        var trackedIds = records.Select(record => record.DesktopId).ToHashSet();
        var all = _desktops.GetAll();
        var safeFallback = all.FirstOrDefault(desktop => !trackedIds.Contains(desktop.Id));
        if (safeFallback is null && records.Any(record => !referenced.Contains(record.DesktopId)))
        {
            safeFallback = _desktops.Create();
            try { _desktops.SetName(safeFallback, "Рабочий стол"); } catch { }
        }

        foreach (var record in records.Where(record => !referenced.Contains(record.DesktopId)))
        {
            var desktop = _desktops.Find(record.DesktopId);
            if (desktop is null)
            {
                _desktopStore.Forget(record.DesktopId);
                continue;
            }

            var fallback = _desktops.Find(record.FallbackId);
            if (fallback is null || fallback.Id == desktop.Id || trackedIds.Contains(fallback.Id))
                fallback = safeFallback;
            if (fallback is null || fallback.Id == desktop.Id)
            {
                AppLogger.Warning($"Для осиротевшего Space {desktop.Id} не найден безопасный стол возврата");
                continue;
            }

            try
            {
                if (_desktops.IsCurrent(desktop)) _desktops.Switch(fallback);
                _desktops.Remove(desktop, fallback);
                if (_desktops.Find(desktop.Id) is null)
                {
                    _desktopStore.Forget(desktop.Id);
                    AppLogger.Info($"Удалён осиротевший Space {desktop.Id}");
                }
            }
            catch (Exception ex) { AppLogger.Error($"Не удалось удалить осиротевший Space {desktop.Id}", ex); }
        }
    }

    private DesktopService.Desktop ResolveRecoveryOrigin(ManagedSession session,
        DesktopService.Desktop dedicated)
    {
        var origin = _desktops.Find(session.OriginDesktopId);
        if (origin is not null && origin.Id != dedicated.Id) return origin;

        var current = _desktops.Current();
        if (current.Id != dedicated.Id) return current;

        var rescue = _desktops.Create();
        try { _desktops.SetName(rescue, "Рабочий стол"); } catch { }
        AppLogger.Warning($"Исходный стол сессии {dedicated.Id} исчез; создан безопасный стол возврата");
        return rescue;
    }

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
        _previous[session.Hwnd] = false;
        _desktopStore.Forget(session.DedicatedDesktopId);
        SaveSessions();
    }

    private void RollbackCreatedSession(ManagedSession? session, DesktopService.Desktop? dedicated,
        DesktopService.Desktop fallback)
    {
        if (dedicated is null) return;
        try
        {
            if (session is not null) MoveAllWindowsToOrigin(session);
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

    private static string GetExecutablePath(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.MainModule?.FileName ?? "";
        }
        catch { return ""; }
    }

    private static bool BelongsToApplication(IntPtr hwnd, ManagedSession session)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == session.ProcessId || IsOwnedBy(hwnd, session.Hwnd)) return true;
        var executablePath = GetExecutablePath(processId);
        return !string.IsNullOrWhiteSpace(executablePath) &&
               !string.IsNullOrWhiteSpace(session.ExecutablePath) &&
               string.Equals(executablePath, session.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameApplication(ManagedSession first, ManagedSession second) =>
        first.ProcessId == second.ProcessId ||
        (!string.IsNullOrWhiteSpace(first.ExecutablePath) &&
         !string.IsNullOrWhiteSpace(second.ExecutablePath) &&
         string.Equals(first.ExecutablePath, second.ExecutablePath, StringComparison.OrdinalIgnoreCase));

    private static bool IsSessionOwnerWindow(IntPtr hwnd, ManagedSession session)
    {
        if (!NativeMethods.IsWindow(hwnd)) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        return processId == session.ProcessId;
    }

    private void ReconcileDedicatedDesktop(ManagedSession session)
    {
        if (session.Origin is null || session.Dedicated is null) return;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (hwnd == session.Hwnd || !NativeMethods.IsWindow(hwnd) || !IsCandidate(hwnd)) return true;
            var isForegroundOnCurrentSpace = hwnd == NativeMethods.GetForegroundWindow() &&
                                             _desktops.IsCurrent(session.Dedicated);
            if (!isForegroundOnCurrentSpace && !_desktops.IsWindowOnDesktop(hwnd, session.Dedicated))
                return true;

            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == (uint)Environment.ProcessId) return true;

            // Keep auxiliary windows from the same application in its Space.
            // Browser extensions and similar popups are often ownerless top-level
            // windows, sometimes even hosted by another process of the same EXE.
            var belongsToApplication = BelongsToApplication(hwnd, session);
            if (!IsFullscreen(hwnd) && !belongsToApplication)
            {
                try
                {
                    var followWindow = hwnd == NativeMethods.GetForegroundWindow();
                    _desktops.MoveWindow(hwnd, session.Origin);
                    if (followWindow && session.Dedicated is not null && _desktops.IsCurrent(session.Dedicated))
                    {
                        _desktops.Switch(session.Origin);
                        NativeMethods.SetForegroundWindow(hwnd);
                    }
                }
                catch (Exception ex) { AppLogger.Warning($"Не удалось вернуть постороннее окно {hwnd}: {ex.Message}"); }
            }
            return true;
        }, IntPtr.Zero);
    }

    private static bool IsOwnedBy(IntPtr hwnd, IntPtr possibleOwner)
    {
        var current = hwnd;
        for (var depth = 0; depth < 16; depth++)
        {
            current = NativeMethods.GetWindow(current, NativeMethods.GwOwner);
            if (current == IntPtr.Zero) return false;
            if (current == possibleOwner) return true;
        }
        return false;
    }

    private void MoveAllWindowsToOrigin(ManagedSession session)
    {
        if (session.Origin is null || session.Dedicated is null) return;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindow(hwnd) || !_desktops.IsWindowOnDesktop(hwnd, session.Dedicated))
                return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == (uint)Environment.ProcessId) return true;
            try { _desktops.MoveWindow(hwnd, session.Origin); }
            catch (Exception ex) { AppLogger.Warning($"Не удалось эвакуировать окно {hwnd}: {ex.Message}"); }
            return true;
        }, IntPtr.Zero);
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

    private static void Enumerate(Action<IntPtr> visitor)
    {
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (IsCandidate(hwnd)) visitor(hwnd);
            return true;
        }, IntPtr.Zero);
    }

    private static bool IsCandidate(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindowVisible(hwnd))
            return false;
        if (NativeMethods.GetWindowTextLength(hwnd) == 0) return false;

        var className = new System.Text.StringBuilder(256);
        NativeMethods.GetClassName(hwnd, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return false;

        var title = new System.Text.StringBuilder(Math.Min(NativeMethods.GetWindowTextLength(hwnd) + 1, 257));
        NativeMethods.GetWindowText(hwnd, title, title.Capacity);
        if (title.ToString().Trim() is "Переключение задач" or "Представление задач" or
            "Task Switching" or "Task View" or "Virtual desktop switching preview" or
            "Desktop switching preview")
            return false;

        NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DwmwaCloaked,
            out var cloaked, Marshal.SizeOf<int>());
        if (cloaked != 0 && !IsFullscreen(hwnd)) return false;

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return pid != (uint)Environment.ProcessId;
    }

    private static bool IsFullscreen(IntPtr hwnd)
    {
        if (NativeMethods.IsIconic(hwnd)) return false;
        if (NativeMethods.IsZoomed(hwnd)) return true;
        if (!NativeMethods.GetWindowRect(hwnd, out var window)) return false;

        var monitor = NativeMethods.MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
        if (monitor == IntPtr.Zero) return false;
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return false;

        const int tolerance = 2;
        return Math.Abs(window.Left - info.Monitor.Left) <= tolerance &&
               Math.Abs(window.Top - info.Monitor.Top) <= tolerance &&
               Math.Abs(window.Right - info.Monitor.Right) <= tolerance &&
               Math.Abs(window.Bottom - info.Monitor.Bottom) <= tolerance;
    }

    private static string GetApplicationName(IntPtr hwnd)
    {
        var titleLength = NativeMethods.GetWindowTextLength(hwnd);
        if (titleLength > 0)
        {
            var title = new System.Text.StringBuilder(Math.Min(titleLength + 1, 513));
            NativeMethods.GetWindowText(hwnd, title, title.Capacity);
            var windowName = SanitizeDesktopName(title.ToString());
            if (!string.IsNullOrWhiteSpace(windowName))
                return windowName.Length <= 64 ? windowName : windowName[..64];
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var version = process.MainModule?.FileVersionInfo;
            var name = version?.FileDescription;
            if (string.IsNullOrWhiteSpace(name)) name = process.ProcessName;
            name = SanitizeDesktopName(name);
            return name.Length <= 64 ? name : name[..64];
        }
        catch
        {
            return "Fullscreen";
        }
    }

    private static string SanitizeDesktopName(string value)
    {
        var cleaned = new string(value.Where(character =>
        {
            var category = char.GetUnicodeCategory(character);
            return category is not System.Globalization.UnicodeCategory.Control and
                not System.Globalization.UnicodeCategory.Format;
        }).ToArray());
        return string.Join(" ", cleaned.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

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

    private static bool IsAutostartEnabled()
    {
        return RunTaskScheduler($"/Query /TN \"{ScheduledTaskName}\"") == 0;
    }

    private static void OpenDesktopMultitaskingSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:multitasking") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось открыть настройки Windows:\n{ex.Message}",
                "FullScreenManager", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetAutostart(bool enabled)
    {
        try
        {
            if (enabled)
            {
                var executable = Environment.ProcessPath
                    ?? throw new InvalidOperationException("Не удалось определить путь приложения.");
                var result = RunTaskScheduler(
                    $"/Create /TN \"{ScheduledTaskName}\" /SC ONLOGON /RL HIGHEST /TR \"\\\"{executable}\\\"\" /F");
                if (result != 0)
                    throw new InvalidOperationException($"Планировщик заданий вернул код {result}.");

                // Remove the old non-elevated startup entry created by versions <= 1.0.2.
                using var oldKey = Registry.CurrentUser.CreateSubKey(RunKeyPath);
                oldKey.DeleteValue(RunValueName, false);
            }
            else
            {
                var result = RunTaskScheduler($"/Delete /TN \"{ScheduledTaskName}\" /F");
                if (result != 0 && result != 1)
                    throw new InvalidOperationException($"Планировщик заданий вернул код {result}.");
                using var oldKey = Registry.CurrentUser.CreateSubKey(RunKeyPath);
                oldKey.DeleteValue(RunValueName, false);
            }
            _autostartItem.Checked = enabled;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось изменить автозапуск:\n{ex.Message}", "FullScreenManager",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static int RunTaskScheduler(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        }) ?? throw new InvalidOperationException("Не удалось запустить Планировщик заданий.");
        process.WaitForExit();
        return process.ExitCode;
    }

    private static void ShowAbout() => MessageBox.Show(
        "FullScreenManager 1.0\n\nМаксимизированные окна на отдельных виртуальных рабочих столах.",
        "О программе", MessageBoxButtons.OK, MessageBoxIcon.Information);

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
                MoveAllWindowsToOrigin(session);
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

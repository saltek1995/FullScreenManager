using System.Diagnostics;
using static FullScreenManager.WindowInspector;
using Timer = System.Windows.Forms.Timer;

namespace FullScreenManager;

internal sealed class ManagerContext : ApplicationContext
{
    private readonly Timer _timer = new() { Interval = 200 };
    private DesktopService _desktops = new();
    private readonly Dictionary<IntPtr, ManagedSession> _sessions = [];
    private readonly HashSet<IntPtr> _suppressedWindows = [];
    private readonly Dictionary<IntPtr, long> _discoveryRetryScan = [];
    private readonly Dictionary<Guid, int> _orphanAbsenceObservations = [];
    private readonly Dictionary<Guid, long> _orphanRetryScan = [];
    private readonly SessionStore _sessionStore = new();
    private readonly ManagedDesktopStore _desktopStore = new();
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _enabledItem;
    private bool _initialDiscovery = true;
    private bool _busy;
    private bool _enabled = true;
    private long _scanNumber;

    public ManagerContext()
    {
        RecoverSessions();
        (_tray, _enabledItem) = TrayUi.Create(ExitThread);
        _enabledItem.CheckedChanged += (_, _) =>
        {
            _enabled = _enabledItem.Checked;
            if (_enabled) _initialDiscovery = true;
        };
        _timer.Tick += Tick;
        _timer.Start();
    }

    private void Tick(object? sender, EventArgs args)
    {
        if (_busy) return;
        _busy = true;
        try { TickCore(); }
        catch (Exception ex)
        {
            AppLogger.Error("Сбой цикла мониторинга; COM-подключение будет восстановлено", ex);
            ReconnectDesktopService();
        }
        finally { _busy = false; }
    }

    private void TickCore()
    {
        _scanNumber++;
        RepairSessionIndex();
        MonitorSessions();
        ReconcileTrackedDesktops();
        if (_enabled) DiscoverFullscreenWindows();
        _initialDiscovery = false;
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
        catch (Exception ex)
        {
            AppLogger.Error("Explorer пока не готов принять новое COM-подключение", ex);
        }
    }

    private void DiscoverFullscreenWindows()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var candidates = new List<WindowCandidate>();
        Enumerate(hwnd =>
        {
            var fullscreen = IsFullscreen(hwnd);
            if (!fullscreen)
            {
                _suppressedWindows.Remove(hwnd);
                _discoveryRetryScan.Remove(hwnd);
                return;
            }
            var suppressed = _suppressedWindows.Contains(hwnd);
            var managed = IsAlreadyManaged(hwnd);
            var retryReady = !_discoveryRetryScan.TryGetValue(hwnd, out var retryAt) || _scanNumber >= retryAt;
            if (!StatePolicy.ShouldDiscover(fullscreen, suppressed, managed,
                    _initialDiscovery, hwnd == foreground, retryReady)) return;
            var origin = _desktops.GetWindowDesktop(hwnd);
            if (origin is not null) candidates.Add(new WindowCandidate(hwnd, origin, hwnd == foreground));
        });

        // Move the foreground window last, then activate only its resulting Space.
        foreach (var candidate in candidates.OrderBy(item => item.IsForeground))
            HandleFullscreenCandidate(candidate);
    }

    private void HandleFullscreenCandidate(WindowCandidate candidate)
    {
        if (!NativeMethods.IsWindow(candidate.Handle) || !IsFullscreen(candidate.Handle) ||
            IsAlreadyManaged(candidate.Handle)) return;

        var host = FindSessionByDesktop(candidate.Origin.Id);
        if (host is not null && BelongsToApplication(candidate.Handle, host))
        {
            _suppressedWindows.Add(candidate.Handle);
            AppLogger.Info($"Вспомогательное полноэкранное окно {candidate.Handle} оставлено в Space {host.DedicatedDesktopId}");
            return;
        }

        TryCreateSession(candidate);
    }

    private void TryCreateSession(WindowCandidate candidate)
    {
        DesktopService.Desktop? dedicated = null;
        ManagedSession? session = null;
        try
        {
            var origin = ResolveUnmanagedOrigin(candidate.Origin);
            if (!NativeMethods.IsWindow(candidate.Handle) || !IsFullscreen(candidate.Handle)) return;

            dedicated = _desktops.Create();
            _desktopStore.Track(dedicated.Id, origin.Id);
            _desktops.MoveAfterPrimary(dedicated);

            NativeMethods.GetWindowThreadProcessId(candidate.Handle, out var processId);
            var name = GetApplicationName(candidate.Handle);
            session = RegisterSession(candidate.Handle, processId, origin, dedicated, name);
            session.NextNameSyncUtc = TrySetDesktopName(dedicated, name)
                ? DateTime.MaxValue
                : DateTime.UtcNow.AddSeconds(5);

            _desktops.MoveWindow(candidate.Handle, dedicated);
            if (!_desktops.IsWindowOnDesktop(candidate.Handle, dedicated))
                throw new InvalidOperationException($"Windows не подтвердила перенос HWND {candidate.Handle} в Space {dedicated.Id}.");

            session.State = SessionState.Active;
            session.UpdatedUtc = DateTime.UtcNow;
            SaveSessions();
            if (candidate.IsForeground)
            {
                _desktops.Switch(dedicated);
                ActivateFullscreenWindow(candidate.Handle, session);
            }
        }
        catch (Exception ex)
        {
            _discoveryRetryScan[candidate.Handle] = _scanNumber + 10;
            AppLogger.Error($"Не удалось создать Space для HWND {candidate.Handle}", ex);
            if (session is not null) BeginCleanup(session, "ошибка создания");
            else if (dedicated is not null) TryRemoveOrphan(dedicated.Id);
            ShowError("Не удалось создать полноэкранный рабочий стол", ex);
        }
    }

    private ManagedSession RegisterSession(IntPtr hwnd, uint processId, DesktopService.Desktop origin,
        DesktopService.Desktop dedicated, string name)
    {
        if (IsAlreadyManaged(hwnd))
            throw new InvalidOperationException($"HWND {hwnd} уже принадлежит активной сессии.");
        if (FindSessionByDesktop(dedicated.Id) is not null)
            throw new InvalidOperationException($"Space {dedicated.Id} уже принадлежит активной сессии.");

        var session = new ManagedSession
        {
            WindowHandle = hwnd.ToInt64(),
            ProcessId = processId,
            ProcessStartedUtc = GetProcessStartedUtc(processId),
            ExecutablePath = GetExecutablePath(processId),
            OriginDesktopId = origin.Id,
            DedicatedDesktopId = dedicated.Id,
            Origin = origin,
            Dedicated = dedicated,
            Name = name,
            State = SessionState.Creating,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            AwaitingFullscreenReactivation = true
        };
        if (!_sessions.TryAdd(hwnd, session))
            throw new InvalidOperationException($"Не удалось зарегистрировать HWND {hwnd}.");
        SaveSessions();
        AppLogger.Info($"Создана сессия {dedicated.Id} для HWND {hwnd} ({name})");
        return session;
    }

    private void MonitorSessions()
    {
        foreach (var session in _sessions.Values.Distinct().ToList())
            MonitorSession(session);
    }

    private void MonitorSession(ManagedSession session)
    {
        var dedicated = _desktops.Find(session.DedicatedDesktopId);
        if (dedicated is null)
        {
            session.MissingDesktopObservations++;
            if (session.MissingDesktopObservations >= StatePolicy.MissingConfirmationCount)
                CompleteSession(session, "Space отсутствует в трёх последовательных снимках");
            return;
        }

        session.MissingDesktopObservations = 0;
        session.Dedicated = dedicated;
        if (!EnsureOrigin(session, dedicated)) return;

        if (session.State != SessionState.Active)
        {
            if (DateTime.UtcNow >= session.NextRetryUtc) CleanupSession(session);
            return;
        }

        if (IsSessionOwnerWindow(session.Hwnd, session))
        {
            var ownerDesktop = _desktops.GetWindowDesktop(session.Hwnd);
            if (ownerDesktop is not null && ownerDesktop.Id != dedicated.Id)
            {
                RestoreOwnerToDedicatedSpace(session);
                return;
            }
            MonitorOwnerWindow(session);
        }
        else MonitorReplacementWindow(session);
    }

    private void RestoreOwnerToDedicatedSpace(ManagedSession session)
    {
        try
        {
            if (session.Dedicated is null || !IsFullscreen(session.Hwnd))
            {
                BeginCleanup(session, "окно покинуло свой Space");
                return;
            }
            var foreground = session.Hwnd == NativeMethods.GetForegroundWindow();
            _desktops.MoveWindow(session.Hwnd, session.Dedicated);
            if (foreground)
            {
                _desktops.Switch(session.Dedicated);
                ActivateFullscreenWindow(session.Hwnd, session);
            }
            AppLogger.Warning($"Сессия {session.DedicatedDesktopId}: окно возвращено в принадлежащий ему Space");
        }
        catch (Exception ex) { ScheduleRetry(session, ex); }
    }

    private bool EnsureOrigin(ManagedSession session, DesktopService.Desktop dedicated)
    {
        try
        {
            var stored = _desktops.Find(session.OriginDesktopId);
            var origin = stored is not null && stored.Id != dedicated.Id
                ? ResolveUnmanagedOrigin(stored)
                : FindSafeOrigin(dedicated.Id);
            if (origin.Id != session.OriginDesktopId)
            {
                session.OriginDesktopId = origin.Id;
                session.UpdatedUtc = DateTime.UtcNow;
                _desktopStore.Track(session.DedicatedDesktopId, origin.Id);
                SaveSessions();
            }
            session.Origin = origin;
            return true;
        }
        catch (Exception ex)
        {
            ScheduleRetry(session, ex);
            return false;
        }
    }

    private void MonitorOwnerWindow(ManagedSession session)
    {
        var hwnd = session.Hwnd;
        var dedicated = session.Dedicated!;
        var current = _desktops.IsCurrent(dedicated);
        var foreground = hwnd == NativeMethods.GetForegroundWindow();
        session.MissingWindowObservations = 0;

        var visible = NativeMethods.IsWindowVisible(hwnd);
        var iconic = NativeMethods.IsIconic(hwnd);
        var fullscreen = visible && !iconic && IsFullscreen(hwnd);
        var clearlyWindowed = visible && !iconic && !fullscreen && IsClearlyWindowed(hwnd);
        var processAlive = IsProcessAlive(session);
        if (session.AwaitingFullscreenReactivation && current && !session.ActivationRequested)
        {
            ActivateFullscreenWindow(hwnd, session);
            return;
        }
        var observation = new WindowObservation(true, visible, iconic, fullscreen, clearlyWindowed,
            current, foreground, session.AwaitingFullscreenReactivation, processAlive,
            IsSharedWindowHost(session.ExecutablePath), 0);
        if (StatePolicy.Decide(observation) == SessionObservationAction.Cleanup)
        {
            var reason = !visible ? "окно скрыто" : iconic ? "окно свёрнуто" : "выход из полноэкранного режима";
            BeginCleanup(session, reason);
            return;
        }

        if (visible)
        {
            WindowMover.Reconcile(_desktops, session);
            UpdateDesktopName(hwnd, session);
        }
        if (fullscreen)
        {
            session.AwaitingFullscreenReactivation = false;
            session.ActivationRequested = false;
        }
        RememberWindowPosition(session, current, foreground);
    }

    private static void RememberWindowPosition(ManagedSession session, bool current, bool foreground)
    {
        if (!current) session.ActivationRequested = false;
        session.WasOnDedicatedDesktop = current;
        session.WasForeground = foreground;
    }

    private void MonitorReplacementWindow(ManagedSession session)
    {
        var replacement = FindReplacementWindow(session);
        if (replacement != IntPtr.Zero)
        {
            RebindSession(session, replacement);
            return;
        }

        session.MissingWindowObservations++;
        var processAlive = IsProcessAlive(session);
        var desktopCurrent = session.Dedicated is not null && _desktops.IsCurrent(session.Dedicated);
        var observation = new WindowObservation(false, false, false, false, false,
            desktopCurrent, false, session.AwaitingFullscreenReactivation, processAlive,
            IsSharedWindowHost(session.ExecutablePath), session.MissingWindowObservations);
        if (StatePolicy.Decide(observation) == SessionObservationAction.Cleanup)
            BeginCleanup(session, "окно закрыто и замена не найдена");
    }

    private IntPtr FindReplacementWindow(ManagedSession session)
    {
        var fullscreenOnSpace = new List<IntPtr>();
        var visibleOnSpace = new List<IntPtr>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd) || IsAlreadyManaged(hwnd)) return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (!IsSameProcessInstance(processId, session)) return true;
            if (session.Dedicated is null || !_desktops.IsWindowOnDesktop(hwnd, session.Dedicated)) return true;
            visibleOnSpace.Add(hwnd);
            if (IsFullscreen(hwnd)) fullscreenOnSpace.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        if (fullscreenOnSpace.Count == 1) return fullscreenOnSpace[0];
        return session.AwaitingFullscreenReactivation && visibleOnSpace.Count == 1
            ? visibleOnSpace[0]
            : IntPtr.Zero;
    }

    private void RebindSession(ManagedSession session, IntPtr replacement)
    {
        var previous = session.Hwnd;
        _sessions.Remove(previous);
        if (_sessions.ContainsKey(replacement))
        {
            _sessions[previous] = session;
            ScheduleRetry(session, new InvalidOperationException($"HWND замены {replacement} уже занят."));
            return;
        }
        session.WindowHandle = replacement.ToInt64();
        session.MissingWindowObservations = 0;
        session.WasForeground = false;
        session.WasOnDedicatedDesktop = false;
        session.AwaitingFullscreenReactivation = true;
        session.ActivationRequested = false;
        session.UpdatedUtc = DateTime.UtcNow;
        _sessions.Add(replacement, session);
        UpdateDesktopName(replacement, session);
        SaveSessions();
        AppLogger.Info($"Сессия {session.DedicatedDesktopId}: HWND заменён {previous} → {replacement}");
    }

    private void BeginCleanup(ManagedSession session, string reason)
    {
        if (session.State != SessionState.Active && session.State != SessionState.Creating) return;
        session.State = SessionState.RetryRequired;
        session.NextRetryUtc = DateTime.UtcNow;
        session.UpdatedUtc = DateTime.UtcNow;
        _suppressedWindows.Add(session.Hwnd);
        SaveSessions();
        AppLogger.Info($"Сессия {session.DedicatedDesktopId}: начата очистка ({reason})");
        CleanupSession(session);
    }

    private void CleanupSession(ManagedSession session)
    {
        if (session.Dedicated is null) return;
        try
        {
            var origin = session.Origin ?? FindSafeOrigin(session.DedicatedDesktopId);
            session.Origin = origin;
            session.OriginDesktopId = origin.Id;
            session.State = SessionState.Removing;
            session.UpdatedUtc = DateTime.UtcNow;
            SaveSessions();

            if (NativeMethods.IsWindow(session.Hwnd) &&
                _desktops.IsWindowOnDesktop(session.Hwnd, session.Dedicated))
                _desktops.MoveWindow(session.Hwnd, origin);
            WindowMover.MoveAll(_desktops, session.Dedicated, origin);
            if (_desktops.IsCurrent(session.Dedicated)) _desktops.Switch(origin);
            _desktops.Remove(session.Dedicated, origin);

            session.State = SessionState.RetryRequired;
            session.RetryCount = 0;
            session.NextRetryUtc = DateTime.UtcNow.AddMilliseconds(500);
            session.UpdatedUtc = DateTime.UtcNow;
            SaveSessions();
        }
        catch (Exception ex) { ScheduleRetry(session, ex); }
    }

    private void CompleteSession(ManagedSession session, string reason)
    {
        foreach (var key in _sessions.Where(pair => ReferenceEquals(pair.Value, session))
                     .Select(pair => pair.Key).ToList())
            _sessions.Remove(key);
        _desktopStore.Forget(session.DedicatedDesktopId);
        _orphanAbsenceObservations.Remove(session.DedicatedDesktopId);
        _orphanRetryScan.Remove(session.DedicatedDesktopId);
        SaveSessions();
        AppLogger.Info($"Сессия {session.DedicatedDesktopId}: завершена ({reason})");
    }

    private void ReconcileTrackedDesktops()
    {
        var sessionDesktopIds = _sessions.Values.Select(session => session.DedicatedDesktopId).ToHashSet();
        var desktops = _desktops.GetAll().ToDictionary(desktop => desktop.Id);
        var confirmedAbsent = new List<Guid>();
        foreach (var record in _desktopStore.Records.Where(record => !sessionDesktopIds.Contains(record.DesktopId)))
        {
            if (!desktops.ContainsKey(record.DesktopId))
            {
                var observations = _orphanAbsenceObservations.GetValueOrDefault(record.DesktopId) + 1;
                _orphanAbsenceObservations[record.DesktopId] = observations;
                if (observations >= StatePolicy.MissingConfirmationCount)
                {
                    confirmedAbsent.Add(record.DesktopId);
                    _orphanAbsenceObservations.Remove(record.DesktopId);
                    _orphanRetryScan.Remove(record.DesktopId);
                }
                continue;
            }

            _orphanAbsenceObservations.Remove(record.DesktopId);
            if (!_orphanRetryScan.TryGetValue(record.DesktopId, out var retryAt) || _scanNumber >= retryAt)
                TryRemoveOrphan(record.DesktopId);
        }
        _desktopStore.ForgetMany(confirmedAbsent);
    }

    private void TryRemoveOrphan(Guid desktopId)
    {
        try
        {
            var desktop = _desktops.Find(desktopId);
            if (desktop is null) return;
            var fallback = FindSafeOrigin(desktopId);
            WindowMover.MoveAll(_desktops, desktop, fallback);
            if (_desktops.IsCurrent(desktop)) _desktops.Switch(fallback);
            _desktops.Remove(desktop, fallback);
            _orphanRetryScan[desktopId] = _scanNumber + 3;
            AppLogger.Info($"Запрошено удаление осиротевшего Space {desktopId}");
        }
        catch (Exception ex)
        {
            _orphanRetryScan[desktopId] = _scanNumber + 25;
            AppLogger.Error($"Не удалось удалить осиротевший Space {desktopId}", ex);
        }
    }

    private DesktopService.Desktop ResolveUnmanagedOrigin(DesktopService.Desktop candidate)
    {
        var visited = new HashSet<Guid>();
        while (visited.Add(candidate.Id))
        {
            var owner = FindSessionByDesktop(candidate.Id);
            if (owner is null) return candidate;
            var parent = owner.Origin ?? _desktops.Find(owner.OriginDesktopId);
            if (parent is null) break;
            candidate = parent;
        }
        return FindSafeOrigin(Guid.Empty);
    }

    private DesktopService.Desktop FindSafeOrigin(Guid excludedDesktopId)
    {
        var managedIds = _desktopStore.Records.Select(record => record.DesktopId).ToHashSet();
        managedIds.Add(excludedDesktopId);
        var safe = _desktops.GetAll().FirstOrDefault(desktop => !managedIds.Contains(desktop.Id));
        if (safe is not null) return safe;
        safe = _desktops.Create();
        TrySetDesktopName(safe, "Рабочий стол");
        AppLogger.Warning($"Создан резервный обычный рабочий стол {safe.Id}");
        return safe;
    }

    private void RecoverSessions()
    {
        foreach (var session in SessionRecovery.Recover(_desktops, _sessionStore, _desktopStore))
        {
            if (IsAlreadyManaged(session.Hwnd) || FindSessionByDesktop(session.DedicatedDesktopId) is not null)
            {
                AppLogger.Warning($"Пропущена дублирующая сессия HWND {session.Hwnd}, Space {session.DedicatedDesktopId}");
                continue;
            }
            _sessions.Add(session.Hwnd, session);
        }
        RepairOriginChains();
        SaveSessions();
    }

    private void RepairOriginChains()
    {
        foreach (var session in _sessions.Values.Distinct())
        {
            if (session.Dedicated is null) continue;
            EnsureOrigin(session, session.Dedicated);
        }
    }

    private void RepairSessionIndex()
    {
        foreach (var pair in _sessions.ToList())
        {
            if (pair.Key == pair.Value.Hwnd) continue;
            _sessions.Remove(pair.Key);
            if (!_sessions.TryAdd(pair.Value.Hwnd, pair.Value))
                BeginCleanup(pair.Value, "конфликт индекса HWND");
        }

        foreach (var duplicate in _sessions.Values.GroupBy(session => session.DedicatedDesktopId)
                     .Where(group => group.Count() > 1).SelectMany(group => group.Skip(1)).ToList())
            BeginCleanup(duplicate, "конфликт владельцев Space");
    }

    private ManagedSession? FindSessionByDesktop(Guid desktopId) =>
        _sessions.Values.Distinct().FirstOrDefault(session => session.DedicatedDesktopId == desktopId);

    private bool IsAlreadyManaged(IntPtr hwnd) =>
        _sessions.ContainsKey(hwnd) || _sessions.Values.Any(session => session.Hwnd == hwnd);

    private static bool IsSharedWindowHost(string executablePath)
    {
        var name = Path.GetFileName(executablePath);
        return name.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("RuntimeBroker.exe", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("ShellExperienceHost.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProcessAlive(ManagedSession session)
    {
        var started = GetProcessStartedUtc(session.ProcessId);
        return started is not null &&
               (session.ProcessStartedUtc is null || started.Value == session.ProcessStartedUtc.Value);
    }

    private static void ActivateFullscreenWindow(IntPtr hwnd, ManagedSession session)
    {
        session.AwaitingFullscreenReactivation = true;
        session.ActivationRequested = true;
        if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindowAsync(hwnd, 9);
        NativeMethods.SetForegroundWindow(hwnd);
    }

    private bool TrySetDesktopName(DesktopService.Desktop desktop, string name)
    {
        try
        {
            _desktops.SetName(desktop, name);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Не удалось назвать Space {desktop.Id}", ex);
            return false;
        }
    }

    private void UpdateDesktopName(IntPtr hwnd, ManagedSession session)
    {
        var currentName = GetApplicationName(hwnd);
        var nameChanged = !string.Equals(currentName, session.Name, StringComparison.Ordinal);
        if (!nameChanged && (session.NextNameSyncUtc == DateTime.MaxValue ||
                             DateTime.UtcNow < session.NextNameSyncUtc)) return;
        try
        {
            if (session.Dedicated is null) return;
            _desktops.SetName(session.Dedicated, currentName);
            session.Name = currentName;
            session.NextNameSyncUtc = DateTime.MaxValue;
            session.UpdatedUtc = DateTime.UtcNow;
            SaveSessions();
        }
        catch (Exception ex)
        {
            session.NextNameSyncUtc = DateTime.UtcNow.AddSeconds(5);
            AppLogger.Error($"Не удалось синхронизировать имя Space {session.DedicatedDesktopId}", ex);
        }
    }

    private void ScheduleRetry(ManagedSession session, Exception exception)
    {
        session.State = SessionState.RetryRequired;
        session.RetryCount++;
        session.NextRetryUtc = DateTime.UtcNow.AddMilliseconds(
            Math.Min(30_000, 500 * Math.Pow(2, Math.Min(session.RetryCount, 6))));
        session.UpdatedUtc = DateTime.UtcNow;
        SaveSessions();
        AppLogger.Error($"Очистка сессии {session.DedicatedDesktopId} будет повторена", exception);
    }

    private void SaveSessions() => _sessionStore.Save(_sessions.Values.Distinct());

    private void ShowError(string message, Exception exception) =>
        _tray.ShowBalloonTip(7000, "FullScreenManager", $"{message}: {exception.Message}", ToolTipIcon.Error);

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        foreach (var session in _sessions.Values.Distinct().ToList())
        {
            try
            {
                if (session.State == SessionState.Active) BeginCleanup(session, "выход из приложения");
                else CleanupSession(session);
            }
            catch (Exception ex) { AppLogger.Error($"Не удалось очистить Space {session.DedicatedDesktopId} при выходе", ex); }
        }
        SaveSessions();
        _tray.Visible = false;
        _tray.Dispose();
        base.ExitThreadCore();
    }

    private sealed record WindowCandidate(IntPtr Handle, DesktopService.Desktop Origin, bool IsForeground);
}

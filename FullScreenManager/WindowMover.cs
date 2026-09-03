namespace FullScreenManager;

internal static class WindowMover
{
    internal static IntPtr Reconcile(DesktopService desktops, ManagedSession session)
    {
        if (session.Origin is null || session.Dedicated is null) return IntPtr.Zero;
        var foreground = NativeMethods.GetForegroundWindow();
        var sourceDesktopWasCurrent = desktops.IsCurrent(session.Dedicated);
        var foregroundToFollow = IntPtr.Zero;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!ShouldInspect(desktops, hwnd, session, foreground, sourceDesktopWasCurrent)) return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == (uint)Environment.ProcessId) return true;
            if (WindowInspector.IsFullscreen(hwnd) || WindowInspector.BelongsToApplication(hwnd, session)) return true;

            try
            {
                desktops.MoveWindow(hwnd, session.Origin);
                if (StatePolicy.ShouldFollowEvacuatedWindow(hwnd == foreground, sourceDesktopWasCurrent))
                    foregroundToFollow = hwnd;
            }
            catch (Exception ex) { AppLogger.Warning($"Не удалось вернуть постороннее окно {hwnd}: {ex.Message}"); }
            return true;
        }, IntPtr.Zero);
        return foregroundToFollow;
    }

    internal static void MoveAllToOrigin(DesktopService desktops, ManagedSession session)
    {
        if (session.Origin is null || session.Dedicated is null) return;
        MoveAll(desktops, session.Dedicated, session.Origin);
    }

    internal static void MoveAll(DesktopService desktops, DesktopService.Desktop source,
        DesktopService.Desktop destination)
    {
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindow(hwnd) || !desktops.IsWindowOnDesktop(hwnd, source)) return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == (uint)Environment.ProcessId) return true;
            try { desktops.MoveWindow(hwnd, destination); }
            catch (Exception ex) { AppLogger.Warning($"Не удалось эвакуировать окно {hwnd}: {ex.Message}"); }
            return true;
        }, IntPtr.Zero);
    }

    private static bool ShouldInspect(DesktopService desktops, IntPtr hwnd, ManagedSession session,
        IntPtr foreground, bool sourceDesktopWasCurrent)
    {
        if (hwnd == session.Hwnd || !NativeMethods.IsWindow(hwnd) || !WindowInspector.IsCandidate(hwnd)) return false;
        return hwnd == foreground && sourceDesktopWasCurrent ||
               desktops.IsWindowOnDesktop(hwnd, session.Dedicated!);
    }
}

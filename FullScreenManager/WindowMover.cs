namespace FullScreenManager;

internal static class WindowMover
{
    internal static void Reconcile(DesktopService desktops, ManagedSession session)
    {
        if (session.Origin is null || session.Dedicated is null) return;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!ShouldInspect(desktops, hwnd, session)) return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == (uint)Environment.ProcessId) return true;
            if (WindowInspector.IsFullscreen(hwnd) || WindowInspector.BelongsToApplication(hwnd, session)) return true;

            try
            {
                desktops.MoveWindow(hwnd, session.Origin);
            }
            catch (Exception ex) { AppLogger.Warning($"Не удалось вернуть постороннее окно {hwnd}: {ex.Message}"); }
            return true;
        }, IntPtr.Zero);
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

    private static bool ShouldInspect(DesktopService desktops, IntPtr hwnd, ManagedSession session)
    {
        if (hwnd == session.Hwnd || !NativeMethods.IsWindow(hwnd) || !WindowInspector.IsCandidate(hwnd)) return false;
        return hwnd == NativeMethods.GetForegroundWindow() && desktops.IsCurrent(session.Dedicated!) ||
               desktops.IsWindowOnDesktop(hwnd, session.Dedicated!);
    }
}

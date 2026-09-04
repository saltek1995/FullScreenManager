using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FullScreenManager;

internal static class WindowInspector
{
    internal static string GetExecutablePath(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.MainModule?.FileName ?? "";
        }
        catch { return ""; }
    }

    internal static bool BelongsToApplication(IntPtr hwnd, ManagedSession session)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        if (IsSameProcessInstance(processId, session) || IsOwnedBy(hwnd, session.Hwnd)) return true;
        var executablePath = GetExecutablePath(processId);
        return !string.IsNullOrWhiteSpace(executablePath) &&
               !string.IsNullOrWhiteSpace(session.ExecutablePath) &&
               string.Equals(executablePath, session.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsSameApplication(ManagedSession first, ManagedSession second) =>
        first.ProcessId == second.ProcessId ||
        (!string.IsNullOrWhiteSpace(first.ExecutablePath) &&
         !string.IsNullOrWhiteSpace(second.ExecutablePath) &&
         string.Equals(first.ExecutablePath, second.ExecutablePath, StringComparison.OrdinalIgnoreCase));

    internal static bool IsSessionOwnerWindow(IntPtr hwnd, ManagedSession session)
    {
        if (!NativeMethods.IsWindow(hwnd)) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        return IsSameProcessInstance(processId, session);
    }

    internal static bool IsSameProcessInstance(uint processId, ManagedSession session)
    {
        if (processId != session.ProcessId) return false;
        if (session.ProcessStartedUtc is null) return true;
        var started = GetProcessStartedUtc(processId);
        return started is not null && started.Value == session.ProcessStartedUtc.Value;
    }

    internal static DateTime? GetProcessStartedUtc(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.StartTime.ToUniversalTime();
        }
        catch { return null; }
    }

    internal static void Enumerate(Action<IntPtr> visitor)
    {
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (IsCandidate(hwnd)) visitor(hwnd);
            return true;
        }, IntPtr.Zero);
    }

    internal static bool IsFullscreen(IntPtr hwnd)
    {
        if (NativeMethods.IsIconic(hwnd)) return false;
        if (NativeMethods.IsZoomed(hwnd)) return true;
        var monitor = NativeMethods.MonitorFromWindow(hwnd, 2);
        if (monitor == IntPtr.Zero) return false;
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return false;

        var dwmResult = NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DwmwaExtendedFrameBounds,
            out NativeMethods.Rect dwmBounds, Marshal.SizeOf<NativeMethods.Rect>());
        if (dwmResult == 0 && CoversMonitor(dwmBounds, info.Monitor)) return true;

        // Exclusive fullscreen and display-mode changes can leave DWM bounds stale
        // while User32 already reports coordinates in the game's active resolution.
        return NativeMethods.GetPhysicalWindowRect(hwnd, out var userBounds) &&
               CoversMonitor(userBounds, info.Monitor);
    }

    internal static bool CoversMonitor(NativeMethods.Rect window, NativeMethods.Rect monitor, int tolerance = 2) =>
        window.Left <= monitor.Left + tolerance &&
        window.Top <= monitor.Top + tolerance &&
        window.Right >= monitor.Right - tolerance &&
        window.Bottom >= monitor.Bottom - tolerance;

    internal static bool RepresentsForegroundWindow(IntPtr candidate, IntPtr foreground) =>
        candidate == foreground || IsOwnedBy(foreground, candidate);

    internal static bool IsClearlyWindowed(IntPtr hwnd)
    {
        if (NativeMethods.IsIconic(hwnd) || NativeMethods.IsZoomed(hwnd) || IsFullscreen(hwnd)) return false;
        const long windowCaption = 0x00C00000;
        const long thickFrame = 0x00040000;
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlStyle).ToInt64();
        return (style & windowCaption) == windowCaption || (style & thickFrame) != 0;
    }

    internal static string GetApplicationName(IntPtr hwnd)
    {
        var windowName = GetWindowName(hwnd);
        if (!string.IsNullOrWhiteSpace(windowName)) return LimitName(windowName);

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var name = process.MainModule?.FileVersionInfo.FileDescription;
            return LimitName(string.IsNullOrWhiteSpace(name) ? process.ProcessName : name);
        }
        catch { return "Fullscreen"; }
    }

    internal static bool IsCandidate(IntPtr hwnd)
    {
        // Games can expose an untitled top-level HWND while entering exclusive
        // fullscreen, so a non-empty caption is not a reliable invariant.
        if (!NativeMethods.IsWindowVisible(hwnd)) return false;
        const long toolWindow = 0x00000080;
        const long noActivate = 0x08000000;
        var extendedStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
        if ((extendedStyle & (toolWindow | noActivate)) != 0) return false;
        var className = ReadClassName(hwnd);
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (IsShellWindow(ReadWindowText(hwnd).Trim())) return false;

        NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DwmwaCloaked,
            out int cloaked, Marshal.SizeOf<int>());
        if (cloaked != 0 && !IsFullscreen(hwnd)) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return pid != (uint)Environment.ProcessId;
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

    private static string GetWindowName(IntPtr hwnd) => SanitizeDesktopName(ReadWindowText(hwnd));
    private static string LimitName(string name)
    {
        var sanitized = SanitizeDesktopName(name);
        return sanitized.Length <= 64 ? sanitized : sanitized[..64];
    }

    private static string ReadClassName(IntPtr hwnd)
    {
        var value = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, value, value.Capacity);
        return value.ToString();
    }

    private static string ReadWindowText(IntPtr hwnd)
    {
        var length = NativeMethods.GetWindowTextLength(hwnd);
        if (length <= 0) return "";
        var value = new StringBuilder(Math.Min(length + 1, 513));
        NativeMethods.GetWindowText(hwnd, value, value.Capacity);
        return value.ToString();
    }

    private static bool IsShellWindow(string title) => title is
        "Переключение задач" or "Представление задач" or "Task Switching" or "Task View" or
        "Virtual desktop switching preview" or "Desktop switching preview" or
        "Наложение Ножниц" or "Snipping Tool overlay" or "Screen snipping" or
        "Интерфейс ввода Windows" or "Windows Input Experience";

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
}

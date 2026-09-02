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
        if (processId == session.ProcessId || IsOwnedBy(hwnd, session.Hwnd)) return true;
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
        return processId == session.ProcessId;
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
        if (!TryGetWindowBounds(hwnd, out var window)) return false;
        var monitor = NativeMethods.MonitorFromWindow(hwnd, 2);
        if (monitor == IntPtr.Zero) return false;
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return false;

        const int tolerance = 2;
        return IsClose(window.Left, info.Monitor.Left, tolerance) &&
               IsClose(window.Top, info.Monitor.Top, tolerance) &&
               IsClose(window.Right, info.Monitor.Right, tolerance) &&
               IsClose(window.Bottom, info.Monitor.Bottom, tolerance);
    }

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
        if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.GetWindowTextLength(hwnd) == 0) return false;
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

    private static bool IsClose(int first, int second, int tolerance) => Math.Abs(first - second) <= tolerance;

    private static bool TryGetWindowBounds(IntPtr hwnd, out NativeMethods.Rect bounds)
    {
        // DWM bounds use physical screen coordinates and avoid GetWindowRect's
        // DPI virtualization. Exclusive mode can disable DWM, so retain the
        // User32 call as a required fallback.
        var result = NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DwmwaExtendedFrameBounds,
            out bounds, Marshal.SizeOf<NativeMethods.Rect>());
        return result == 0 || NativeMethods.GetPhysicalWindowRect(hwnd, out bounds);
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
}

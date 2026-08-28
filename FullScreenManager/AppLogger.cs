using System.Text;

namespace FullScreenManager;

internal static class AppLogger
{
    private static readonly object Sync = new();

    internal static void Info(string message) => Write("INFO", message);
    internal static void Warning(string message) => Write("WARN", message);
    internal static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message} | {exception}");

    private static void Write(string level, string message)
    {
        try
        {
            AppPaths.EnsureCreated();
            lock (Sync)
            {
                RotateIfNeeded();
                File.AppendAllText(AppPaths.LogFile,
                    $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { }
    }

    private static void RotateIfNeeded()
    {
        var file = new FileInfo(AppPaths.LogFile);
        if (!file.Exists || file.Length < 2 * 1024 * 1024) return;
        var previous = AppPaths.LogFile + ".1";
        if (File.Exists(previous)) File.Delete(previous);
        File.Move(AppPaths.LogFile, previous);
    }
}

namespace FullScreenManager;

internal static class AppPaths
{
    internal static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FullScreenManager");
    internal static readonly string SessionsFile = Path.Combine(DataDirectory, "sessions.json");
    internal static readonly string ManagedDesktopsFile = Path.Combine(DataDirectory, "managed-desktops.json");
    internal static readonly string SettingsFile = Path.Combine(DataDirectory, "settings.json");
    internal static readonly string LogFile = Path.Combine(DataDirectory, "FullScreenManager.log");

    internal static void EnsureCreated() => Directory.CreateDirectory(DataDirectory);
}

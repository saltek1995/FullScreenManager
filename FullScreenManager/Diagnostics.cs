using System.Text.Json;

namespace FullScreenManager;

internal static class Diagnostics
{
    internal static void WriteSnapshot()
    {
        var desktops = new DesktopService();
        var currentId = desktops.Current().Id;
        var allDesktops = desktops.GetAll();
        var desktopIds = allDesktops.Select(desktop => desktop.Id).ToHashSet();
        var sessions = new SessionStore().Load();
        var tracked = new ManagedDesktopStore().Records;
        var snapshot = new
        {
            CapturedUtc = DateTimeOffset.UtcNow,
            CurrentDesktopId = currentId,
            Desktops = allDesktops.Select(desktop => new
            {
                DesktopId = desktop.Id,
                Name = desktops.GetName(desktop),
                IsCurrent = desktop.Id == currentId,
                Session = sessions.FirstOrDefault(session => session.DedicatedDesktopId == desktop.Id),
                Tracking = tracked.FirstOrDefault(record => record.DesktopId == desktop.Id)
            }),
            MissingSessionDesktops = sessions.Where(session => !desktopIds.Contains(session.DedicatedDesktopId)),
            MissingTrackedDesktops = tracked.Where(record => !desktopIds.Contains(record.DesktopId))
        };
        AppPaths.EnsureCreated();
        File.WriteAllText(AppPaths.DiagnosticsFile, JsonSerializer.Serialize(snapshot,
            new JsonSerializerOptions { WriteIndented = true }));
    }
}

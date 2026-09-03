using System.Text.Json;
using System.Text.RegularExpressions;

namespace FullScreenManager;

internal sealed class ManagedDesktopRecord
{
    public Guid DesktopId { get; set; }
    public Guid FallbackId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

internal sealed class ManagedDesktopStore
{
    private readonly Dictionary<Guid, ManagedDesktopRecord> _records = [];

    internal ManagedDesktopStore()
    {
        try
        {
            if (File.Exists(AppPaths.ManagedDesktopsFile))
            {
                foreach (var record in JsonSerializer.Deserialize<List<ManagedDesktopRecord>>(
                             File.ReadAllText(AppPaths.ManagedDesktopsFile)) ?? [])
                    _records[record.DesktopId] = record;
            }
            ImportLegacyLogOnce();
        }
        catch (Exception ex) { AppLogger.Error("Не удалось загрузить реестр управляемых Space", ex); }
    }

    internal IReadOnlyList<ManagedDesktopRecord> Records => _records.Values.ToList();

    internal void Track(Guid desktopId, Guid fallbackId)
    {
        var changed = false;
        if (!_records.TryGetValue(desktopId, out var record))
        {
            _records[desktopId] = new ManagedDesktopRecord { DesktopId = desktopId, FallbackId = fallbackId };
            changed = true;
        }
        else if (fallbackId != Guid.Empty && record.FallbackId != fallbackId)
        {
            record.FallbackId = fallbackId;
            changed = true;
        }
        if (changed) Save();
    }

    internal void Forget(Guid desktopId)
    {
        if (_records.Remove(desktopId)) Save();
    }

    internal void ForgetMany(IEnumerable<Guid> desktopIds)
    {
        var changed = false;
        foreach (var desktopId in desktopIds) changed |= _records.Remove(desktopId);
        if (changed) Save();
    }

    private void ImportLegacyLogOnce()
    {
        if (File.Exists(AppPaths.LegacyImportMarker)) return;
        if (File.Exists(AppPaths.LogFile))
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(AppPaths.LogFile),
                         @"Создана сессия\s+([0-9a-fA-F-]{36})"))
                if (Guid.TryParse(match.Groups[1].Value, out var id) && !_records.ContainsKey(id))
                    _records[id] = new ManagedDesktopRecord { DesktopId = id };
            Save();
        }
        AppPaths.EnsureCreated();
        File.WriteAllText(AppPaths.LegacyImportMarker, DateTimeOffset.UtcNow.ToString("O"));
    }

    private void Save()
    {
        AppPaths.EnsureCreated();
        if (_records.Count == 0)
        {
            if (File.Exists(AppPaths.ManagedDesktopsFile)) File.Delete(AppPaths.ManagedDesktopsFile);
            return;
        }
        var temp = AppPaths.ManagedDesktopsFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_records.Values,
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, AppPaths.ManagedDesktopsFile, true);
    }
}

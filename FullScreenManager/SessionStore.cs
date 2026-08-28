using System.Text.Json;
using System.Text.Json.Serialization;

namespace FullScreenManager;

internal sealed class SessionStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal IReadOnlyList<ManagedSession> Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SessionsFile)) return [];
            return JsonSerializer.Deserialize<List<ManagedSession>>(
                File.ReadAllText(AppPaths.SessionsFile), Options) ?? [];
        }
        catch (Exception ex)
        {
            AppLogger.Error("Не удалось прочитать журнал сессий", ex);
            return [];
        }
    }

    internal void Save(IEnumerable<ManagedSession> sessions)
    {
        try
        {
            AppPaths.EnsureCreated();
            var data = sessions.ToList();
            if (data.Count == 0)
            {
                if (File.Exists(AppPaths.SessionsFile)) File.Delete(AppPaths.SessionsFile);
                return;
            }

            var temp = AppPaths.SessionsFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data, Options));
            File.Move(temp, AppPaths.SessionsFile, true);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Не удалось сохранить журнал сессий", ex);
        }
    }
}

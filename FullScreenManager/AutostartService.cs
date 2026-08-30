using System.Diagnostics;
using Microsoft.Win32;

namespace FullScreenManager;

internal static class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "FullScreenManager";
    private const string TaskName = "FullScreenManager";

    internal static bool IsEnabled() => Run($"/Query /TN \"{TaskName}\"") == 0;

    internal static void SetEnabled(bool enabled)
    {
        var result = enabled ? Create() : Run($"/Delete /TN \"{TaskName}\" /F");
        if (result != 0 && (enabled || result != 1))
            throw new InvalidOperationException($"Планировщик заданий вернул код {result}.");
        using var oldKey = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        oldKey.DeleteValue(RunValueName, false);
    }

    private static int Create()
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь приложения.");
        return Run($"/Create /TN \"{TaskName}\" /SC ONLOGON /RL HIGHEST /TR \"\\\"{executable}\\\"\" /F");
    }

    private static int Run(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        }) ?? throw new InvalidOperationException("Не удалось запустить Планировщик заданий.");
        process.WaitForExit();
        return process.ExitCode;
    }
}

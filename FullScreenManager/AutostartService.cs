using System.Diagnostics;
using System.Security.Principal;
using System.Xml.Linq;
using Microsoft.Win32;

namespace FullScreenManager;

internal static class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "FullScreenManager";
    private const string TaskName = "FullScreenManager";
    private static readonly XNamespace TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    internal static bool IsEnabled()
    {
        if (Run("/Query", "/TN", TaskName) != 0) return false;
        return Create() == 0;
    }

    internal static void SetEnabled(bool enabled)
    {
        var result = enabled ? Create() : Run("/Delete", "/TN", TaskName, "/F");
        if (result != 0 && (enabled || result != 1))
            throw new InvalidOperationException($"Планировщик заданий вернул код {result}.");

        using var oldKey = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        oldKey.DeleteValue(RunValueName, false);
    }

    private static int Create()
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь приложения.");
        var taskFile = Path.Combine(Path.GetTempPath(), $"FullScreenManager-{Guid.NewGuid():N}.xml");
        try
        {
            new XDocument(new XDeclaration("1.0", "utf-16", null), CreateTask(executable)).Save(taskFile);
            return Run("/Create", "/TN", TaskName, "/XML", taskFile, "/F");
        }
        finally
        {
            try { File.Delete(taskFile); }
            catch (Exception ex) { AppLogger.Warning($"Не удалось удалить временный XML автозапуска: {ex.Message}"); }
        }
    }

    private static XElement CreateTask(string executable)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var userId = identity.User?.Value ?? identity.Name;
        return new XElement(TaskNamespace + "Task", new XAttribute("version", "1.4"),
            new XElement(TaskNamespace + "RegistrationInfo",
                new XElement(TaskNamespace + "Description", "Start FullScreenManager at user logon")),
            new XElement(TaskNamespace + "Triggers",
                new XElement(TaskNamespace + "LogonTrigger",
                    new XElement(TaskNamespace + "Enabled", true),
                    new XElement(TaskNamespace + "UserId", userId))),
            new XElement(TaskNamespace + "Principals",
                new XElement(TaskNamespace + "Principal", new XAttribute("id", "Author"),
                    new XElement(TaskNamespace + "UserId", userId),
                    new XElement(TaskNamespace + "LogonType", "InteractiveToken"),
                    new XElement(TaskNamespace + "RunLevel", "HighestAvailable"))),
            new XElement(TaskNamespace + "Settings",
                new XElement(TaskNamespace + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(TaskNamespace + "DisallowStartIfOnBatteries", false),
                new XElement(TaskNamespace + "StopIfGoingOnBatteries", false),
                new XElement(TaskNamespace + "StartWhenAvailable", true),
                new XElement(TaskNamespace + "Enabled", true),
                new XElement(TaskNamespace + "ExecutionTimeLimit", "PT0S")),
            new XElement(TaskNamespace + "Actions", new XAttribute("Context", "Author"),
                new XElement(TaskNamespace + "Exec",
                    new XElement(TaskNamespace + "Command", executable),
                    new XElement(TaskNamespace + "WorkingDirectory", Path.GetDirectoryName(executable) ?? ""))));
    }

    private static int Run(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Не удалось запустить Планировщик заданий.");
        process.WaitForExit();
        return process.ExitCode;
    }
}

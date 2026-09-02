using System.Threading;

namespace FullScreenManager;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (IsRemoveCommand(args)) return RemoveDesktop(args);
        if (args.Contains("--self-test-ui", StringComparer.OrdinalIgnoreCase)) return RunUiSelfTest();
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase)) return RunSelfTest(args);

        using var mutex = new Mutex(true, "Local\\FullScreenManager.Singleton", out var firstInstance);
        if (!firstInstance) return ReportExistingInstance();
        Application.Run(new ManagerContext());
        return 0;
    }

    private static bool IsRemoveCommand(string[] args) =>
        args.Length == 3 && args[0].Equals("--remove-desktop", StringComparison.OrdinalIgnoreCase);

    private static int RunUiSelfTest()
    {
        try { AboutDialog.RunLayoutSelfTest(); return 0; }
        catch (Exception ex) { WriteFailure("FullScreenManager-ui-selftest.log", ex); return 4; }
    }

    private static int RemoveDesktop(string[] args)
    {
        try
        {
            new DesktopService().RemoveById(Guid.Parse(args[1]), Guid.Parse(args[2]));
            return 0;
        }
        catch (Exception ex) { WriteFailure("FullScreenManager-remove.log", ex); return 3; }
    }

    private static int RunSelfTest(string[] args)
    {
        try
        {
            DesktopService.RunSelfTest(ParseWindowHandle(args));
            return 0;
        }
        catch (Exception ex) { WriteFailure("FullScreenManager-selftest.log", ex); return 2; }
    }

    private static IntPtr ParseWindowHandle(string[] args)
    {
        var index = Array.FindIndex(args, value => value.Equals("--hwnd", StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? new IntPtr(long.Parse(args[index + 1])) : IntPtr.Zero;
    }

    private static void WriteFailure(string fileName, Exception exception)
    {
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), fileName), exception.ToString()); }
        catch (Exception logError) { AppLogger.Error("Не удалось записать аварийный журнал", logError); }
    }

    private static int ReportExistingInstance()
    {
        MessageBox.Show("FullScreenManager уже запущен.", "FullScreenManager",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 1;
    }
}

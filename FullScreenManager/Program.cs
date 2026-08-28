using System.Threading;

namespace FullScreenManager;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Length == 3 && args[0].Equals("--remove-desktop", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                new DesktopService().RemoveById(Guid.Parse(args[1]), Guid.Parse(args[2]));
                return 0;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "FullScreenManager-remove.log"), ex.ToString()); }
                catch { }
                return 3;
            }
        }

        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var hwndIndex = Array.FindIndex(args, value =>
                    value.Equals("--hwnd", StringComparison.OrdinalIgnoreCase));
                var hwnd = hwndIndex >= 0 && hwndIndex + 1 < args.Length
                    ? new IntPtr(long.Parse(args[hwndIndex + 1]))
                    : IntPtr.Zero;
                DesktopService.RunSelfTest(hwnd);
                return 0;
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "FullScreenManager-selftest.log"), ex.ToString());
                }
                catch { }
                return 2;
            }
        }

        using var mutex = new Mutex(true, "Local\\FullScreenManager.Singleton", out var firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("FullScreenManager уже запущен.", "FullScreenManager",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }

        Application.Run(new ManagerContext());
        return 0;
    }
}

namespace FullScreenManager;

internal static class TrayUi
{
    internal static (NotifyIcon Tray, ToolStripMenuItem EnabledItem) Create(Action exit)
    {
        var enabledItem = new ToolStripMenuItem("Включено") { Checked = true, CheckOnClick = true };
        var autostartItem = new ToolStripMenuItem("Запускать вместе с Windows")
        {
            Checked = AutostartService.IsEnabled()
        };
        autostartItem.Click += (_, _) => SetAutostart(autostartItem, !autostartItem.Checked);

        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            ShowCheckMargin = true,
            Padding = new Padding(2)
        };
        menu.Items.Add(enabledItem);
        menu.Items.Add(autostartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("О программе", null, (_, _) => ShowAbout());
        menu.Items.Add("Выход", null, (_, _) => exit());

        var executable = Environment.ProcessPath!;
        var tray = new NotifyIcon
        {
            Text = "FullScreenManager — включено",
            Icon = Icon.ExtractAssociatedIcon(executable) ?? SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        enabledItem.CheckedChanged += (_, _) => tray.Text = enabledItem.Checked
            ? "FullScreenManager — включено"
            : "FullScreenManager — приостановлено";
        return (tray, enabledItem);
    }

    private static void SetAutostart(ToolStripMenuItem item, bool enabled)
    {
        try { AutostartService.SetEnabled(enabled); item.Checked = enabled; }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось изменить автозапуск:\n{ex.Message}", "FullScreenManager",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void ShowAbout()
    {
        using var dialog = new AboutDialog();
        dialog.ShowDialog();
    }
}

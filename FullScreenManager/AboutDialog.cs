using System.Diagnostics;
using System.Reflection;

namespace FullScreenManager;

internal sealed class AboutDialog : Form
{
    private const string ProjectUrl = "https://github.com/saltek1995/FullScreenManager";

    internal AboutDialog()
    {
        Text = "О программе";
        Font = SystemFonts.MessageBoxFont;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 270);
        BackColor = Color.White;
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);

        var icon = new PictureBox
        {
            Image = Icon?.ToBitmap(),
            Location = new Point(28, 30),
            Size = new Size(64, 64),
            SizeMode = PictureBoxSizeMode.Zoom
        };
        var title = new Label
        {
            Text = "FullScreenManager",
            Font = new Font(Font.FontFamily, 18, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(116, 28)
        };
        var version = new Label
        {
            Text = $"Версия {GetVersion()}",
            ForeColor = SystemColors.GrayText,
            AutoSize = true,
            Location = new Point(119, 66)
        };
        var description = new Label
        {
            Text = "Полноэкранные приложения — на отдельных\nвиртуальных рабочих столах Windows.",
            AutoSize = true,
            Location = new Point(119, 102)
        };
        var projectLink = new LinkLabel
        {
            Text = "GitHub • исходный код и релизы",
            AutoSize = true,
            Location = new Point(119, 159),
            LinkColor = Color.FromArgb(0, 102, 204)
        };
        projectLink.LinkClicked += (_, _) => OpenProjectPage();

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 64, BackColor = SystemColors.Control };
        var close = new Button
        {
            Text = "Закрыть",
            DialogResult = DialogResult.OK,
            Size = new Size(112, 34),
            Location = new Point(384, 15)
        };
        footer.Controls.Add(close);
        Controls.AddRange([icon, title, version, description, projectLink, footer]);
        AcceptButton = close;
        CancelButton = close;
    }

    private static string GetVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    private static void OpenProjectPage()
    {
        try { Process.Start(new ProcessStartInfo(ProjectUrl) { UseShellExecute = true }); }
        catch (Exception ex) { AppLogger.Error("Не удалось открыть страницу проекта", ex); }
    }
}

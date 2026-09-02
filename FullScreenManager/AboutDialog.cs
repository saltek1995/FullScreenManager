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
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(680, 420);
        MinimumSize = Size;
        BackColor = Color.White;
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);

        var closeButton = CreateCloseButton();
        Controls.Add(CreateLayout(closeButton));
        AcceptButton = closeButton;
        CancelButton = closeButton;
    }

    internal static void RunLayoutSelfTest()
    {
        using var dialog = new AboutDialog
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Opacity = 0
        };
        dialog.Show();
        dialog.PerformLayout();
        Application.DoEvents();
        ValidateChildren(dialog);
        var screenshotPath = Environment.GetEnvironmentVariable("FULLSCREENMANAGER_UI_SCREENSHOT");
        if (!string.IsNullOrWhiteSpace(screenshotPath))
        {
            using var bitmap = new Bitmap(dialog.Width, dialog.Height);
            dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(screenshotPath);
        }
        dialog.Close();
    }

    private static void ValidateChildren(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (child.Right > parent.ClientSize.Width || child.Bottom > parent.ClientSize.Height)
                throw new InvalidOperationException(
                    $"Элемент {child.GetType().Name} '{child.Text}' {child.Bounds} выходит за границы " +
                    $"{parent.GetType().Name} {parent.ClientSize}.");
            ValidateChildren(child);
        }
    }

    private Control CreateLayout(Button closeButton)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(CreateContent(), 0, 0);
        root.Controls.Add(CreateFooter(closeButton), 0, 1);
        return root;
    }

    private Control CreateContent()
    {
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(36, 34, 36, 28),
            Margin = Padding.Empty
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var icon = new PictureBox
        {
            Image = Icon?.ToBitmap(),
            Dock = DockStyle.Top,
            Size = new Size(64, 64),
            Margin = new Padding(0, 2, 18, 0),
            SizeMode = PictureBoxSizeMode.Zoom
        };
        content.Controls.Add(icon, 0, 0);
        content.Controls.Add(CreateTextContent(), 1, 0);
        return content;
    }

    private Control CreateTextContent()
    {
        var text = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty
        };
        text.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var index = 0; index < 4; index++) text.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = "FullScreenManager",
            Font = new Font(Font.FontFamily, 18, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 4)
        };
        var version = new Label
        {
            Text = $"Версия {GetVersion()}",
            ForeColor = SystemColors.GrayText,
            AutoSize = true,
            Margin = new Padding(2, 0, 0, 22)
        };
        var description = new Label
        {
            Text = "Отдельные рабочие столы для\nполноэкранных приложений Windows.",
            AutoSize = true,
            Margin = new Padding(2, 0, 0, 22)
        };
        var link = new LinkLabel
        {
            Text = "GitHub — исходный код и релизы",
            AutoSize = true,
            LinkColor = Color.FromArgb(0, 102, 204),
            Margin = new Padding(2, 0, 0, 0)
        };
        link.LinkClicked += (_, _) => OpenProjectPage();
        text.Controls.Add(title, 0, 0);
        text.Controls.Add(version, 0, 1);
        text.Controls.Add(description, 0, 2);
        text.Controls.Add(link, 0, 3);
        return text;
    }

    private static Control CreateFooter(Button closeButton)
    {
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(24, 14, 24, 14),
            Margin = Padding.Empty,
            BackColor = SystemColors.Control
        };
        footer.Controls.Add(closeButton);
        return footer;
    }

    private static Button CreateCloseButton() => new()
    {
        Text = "Закрыть",
        DialogResult = DialogResult.OK,
        AutoSize = true,
        MinimumSize = new Size(120, 36),
        Margin = Padding.Empty
    };

    private static string GetVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    private static void OpenProjectPage()
    {
        try { Process.Start(new ProcessStartInfo(ProjectUrl) { UseShellExecute = true }); }
        catch (Exception ex) { AppLogger.Error("Не удалось открыть страницу проекта", ex); }
    }
}

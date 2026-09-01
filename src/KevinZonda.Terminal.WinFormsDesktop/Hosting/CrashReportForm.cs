using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace KevinZonda.Terminal.Hosting;

internal sealed class CrashReportForm : Form
{
    private static readonly Color BackgroundColor = Color.FromArgb(23, 27, 34);
    private static readonly Color SurfaceColor = Color.FromArgb(31, 36, 48);
    private static readonly Color ForegroundColor = Color.FromArgb(216, 222, 233);
    private static readonly Color MutedColor = Color.FromArgb(170, 179, 192);
    private readonly string _reportPath;

    internal CrashReportForm(string reportPath, int exitCode, int crashCount)
    {
        _reportPath = reportPath;
        Text = "KevinZonda Terminal crashed";
        BackColor = BackgroundColor;
        ForeColor = ForegroundColor;
        ClientSize = new Size(720, 500);
        MinimumSize = new Size(620, 420);
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        ShowInTaskbar = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(24),
            BackColor = BackgroundColor
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = ForegroundColor,
            Text = crashCount >= 3
                ? "KevinZonda Terminal has crashed repeatedly"
                : "KevinZonda Terminal closed unexpectedly"
        };
        layout.Controls.Add(title, 0, 0);

        var summary = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(660, 0),
            Margin = new Padding(0, 12, 0, 16),
            ForeColor = MutedColor,
            Text = "The terminal sessions from the crashed window cannot be recovered. " +
                "You can restart KevinZonda Terminal in the same working directory or inspect the crash report."
        };
        layout.Controls.Add(summary, 0, 1);

        var details = new TextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Multiline = true,
            MinimumSize = new Size(0, 180),
            BackColor = SurfaceColor,
            ForeColor = ForegroundColor,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font(FontFamily.GenericMonospace, 9F),
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Text = BuildReportPreview(reportPath, exitCode)
        };
        layout.Controls.Add(details, 0, 2);

        var tools = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 14, 0, 0),
            BackColor = BackgroundColor
        };
        var openReport = CreateButton("Open crash report");
        openReport.Click += (_, _) => OpenReport();
        var copyDetails = CreateButton("Copy details");
        copyDetails.Click += (_, _) => CopyDetails();
        tools.Controls.Add(openReport);
        tools.Controls.Add(copyDetails);
        layout.Controls.Add(tools, 0, 3);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 16, 0, 0),
            BackColor = BackgroundColor
        };
        var close = CreateButton("Close");
        close.DialogResult = DialogResult.Cancel;
        var restart = CreateButton("Restart KevinZonda Terminal");
        restart.DialogResult = DialogResult.Retry;
        actions.Controls.Add(close);
        actions.Controls.Add(restart);
        layout.Controls.Add(actions, 0, 4);

        AcceptButton = restart;
        CancelButton = close;
        Controls.Add(layout);
    }

    protected override void OnLoad(EventArgs eventArgs)
    {
        base.OnLoad(eventArgs);
        var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(
            workingArea.Left + Math.Max(0, (workingArea.Width - Width) / 2),
            workingArea.Top + Math.Max(0, (workingArea.Height - Height) / 2));
    }

    private static Button CreateButton(string text) => new()
    {
        AutoSize = true,
        BackColor = SurfaceColor,
        ForeColor = ForegroundColor,
        FlatStyle = FlatStyle.Flat,
        Padding = new Padding(8, 3, 8, 3),
        Text = text,
        UseVisualStyleBackColor = false
    };

    private static string BuildReportPreview(string reportPath, int exitCode)
    {
        var header =
            $"Exit code: {exitCode} (0x{unchecked((uint)exitCode).ToString("X8", CultureInfo.InvariantCulture)})" +
            Environment.NewLine + $"Crash report: {reportPath}";
        try
        {
            return File.Exists(reportPath)
                ? header + Environment.NewLine + Environment.NewLine + File.ReadAllText(reportPath)
                : header + Environment.NewLine + Environment.NewLine + "Crash report is unavailable.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return header + Environment.NewLine + Environment.NewLine +
                $"KevinZonda Terminal could not read the crash report: {exception.Message}";
        }
    }

    private void OpenReport()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _reportPath,
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or
                FileNotFoundException or
                InvalidOperationException)
        {
            MessageBox.Show(
                this,
                $"KevinZonda Terminal could not open the crash report.\n\n{exception.Message}",
                "KevinZonda Terminal crash report",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void CopyDetails()
    {
        try
        {
            var report = File.Exists(_reportPath)
                ? File.ReadAllText(_reportPath)
                : $"Crash report unavailable: {_reportPath}";
            Clipboard.SetText(report);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ExternalException)
        {
            MessageBox.Show(
                this,
                $"KevinZonda Terminal could not copy the crash details.\n\n{exception.Message}",
                "KevinZonda Terminal crash report",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}

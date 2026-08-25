using System.Runtime.InteropServices;

namespace KevinZonda.Terminal.Server.Launcher;

internal sealed class LogsForm : Form
{
    private const int MaximumDisplayedCharacters = 2_000_000;
    private readonly LauncherLogBuffer _logs;
    private readonly RichTextBox _logView;
    private readonly ToolStripButton _autoScrollButton;
    private bool _allowClose;

    internal LogsForm(LauncherLogBuffer logs)
    {
        _logs = logs;
        Text = "KTerm Server Logs";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(640, 360);
        Size = new Size(960, 640);

        var toolbar = new ToolStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden
        };
        var clearButton = new ToolStripButton("Clear");
        clearButton.Click += (_, _) => _logs.Clear();
        var copyButton = new ToolStripButton("Copy All");
        copyButton.Click += (_, _) => CopyAll();
        _autoScrollButton = new ToolStripButton("Auto Scroll")
        {
            CheckOnClick = true,
            Checked = true
        };
        toolbar.Items.Add(clearButton);
        toolbar.Items.Add(copyButton);
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(_autoScrollButton);

        _logView = new RichTextBox
        {
            BackColor = Color.FromArgb(24, 24, 24),
            BorderStyle = BorderStyle.None,
            DetectUrls = false,
            Dock = DockStyle.Fill,
            Font = new Font("Cascadia Mono", 9F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.Gainsboro,
            ReadOnly = true,
            WordWrap = false
        };

        Controls.Add(_logView);
        Controls.Add(toolbar);

        _ = Handle;
        var existingEntries = _logs.Subscribe(ReceiveEntry, ReceiveClear);
        foreach (var entry in existingEntries)
        {
            AppendEntry(entry);
        }
    }

    internal void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _logs.Unsubscribe(ReceiveEntry, ReceiveClear);
        }
        base.Dispose(disposing);
    }

    private void ReceiveEntry(LauncherLogEntry entry)
    {
        if (IsDisposed)
        {
            return;
        }
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(() => AppendEntry(entry));
            }
            catch (InvalidOperationException)
            {
            }
            return;
        }
        AppendEntry(entry);
    }

    private void ReceiveClear()
    {
        if (IsDisposed)
        {
            return;
        }
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(_logView.Clear);
            }
            catch (InvalidOperationException)
            {
            }
            return;
        }
        _logView.Clear();
    }

    private void AppendEntry(LauncherLogEntry entry)
    {
        if (IsDisposed)
        {
            return;
        }

        var label = entry.Source switch
        {
            LauncherLogSource.StandardOutput => "OUT",
            LauncherLogSource.StandardError => "ERR",
            _ => "SYS"
        };
        var color = entry.Source switch
        {
            LauncherLogSource.StandardError => Color.Salmon,
            LauncherLogSource.System => Color.DeepSkyBlue,
            _ => Color.Gainsboro
        };
        var text = $"[{entry.Timestamp:HH:mm:ss.fff}] [{label}] {entry.Message}{Environment.NewLine}";
        _logView.SelectionStart = _logView.TextLength;
        _logView.SelectionLength = 0;
        _logView.SelectionColor = color;
        _logView.AppendText(text);

        if (_logView.TextLength > MaximumDisplayedCharacters)
        {
            _logView.Select(0, _logView.TextLength - MaximumDisplayedCharacters);
            _logView.SelectedText = string.Empty;
        }
        if (_autoScrollButton.Checked)
        {
            _logView.SelectionStart = _logView.TextLength;
            _logView.ScrollToCaret();
        }
    }

    private void CopyAll()
    {
        if (_logView.TextLength == 0)
        {
            return;
        }
        try
        {
            Clipboard.SetText(_logView.Text);
        }
        catch (ExternalException exception)
        {
            MessageBox.Show(
                this,
                $"Unable to copy the log to the clipboard.\n\n{exception.Message}",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }
}

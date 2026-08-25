namespace KevinZonda.Terminal.Server.Launcher;

internal sealed class SettingsForm : Form
{
    private readonly CheckBox _autoStart = new() { AutoSize = true, Text = "Start Server with Launcher" };
    private readonly TextBox _urls = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _authMode = new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly TextBox _workingDirectory = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _runtimeRetention = new()
    {
        DecimalPlaces = 1,
        Dock = DockStyle.Left,
        Increment = 1,
        Maximum = 1440,
        Minimum = 0.1M,
        Width = 140
    };
    private readonly TextBox _additionalArguments = new()
    {
        AcceptsReturn = true,
        AcceptsTab = false,
        Dock = DockStyle.Fill,
        Multiline = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false
    };

    internal SettingsForm(
        LauncherConfiguration configuration,
        string configurationPath)
    {
        Text = "KTerm Server Launcher Settings";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(620, 460);
        Size = new Size(720, 560);
        ShowInTaskbar = true;

        _authMode.Items.AddRange(["auto", "required", "disabled"]);
        _autoStart.Checked = configuration.AutoStart;
        _urls.Text = configuration.Server.Urls;
        _authMode.SelectedItem = configuration.Server.AuthMode;
        if (_authMode.SelectedIndex < 0)
        {
            _authMode.SelectedItem = "auto";
        }
        _workingDirectory.Text = configuration.Server.WorkingDirectory ?? string.Empty;
        _runtimeRetention.Value = Math.Clamp(
            (decimal)configuration.Server.RuntimeRetentionMinutes,
            _runtimeRetention.Minimum,
            _runtimeRetention.Maximum);
        _additionalArguments.Lines = configuration.Server.AdditionalArguments;

        var workingDirectoryPanel = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        };
        workingDirectoryPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        workingDirectoryPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var browseButton = new Button { AutoSize = true, Text = "Browse..." };
        browseButton.Click += (_, _) => BrowseWorkingDirectory();
        workingDirectoryPanel.Controls.Add(_workingDirectory, 0, 0);
        workingDirectoryPanel.Controls.Add(browseButton, 1, 0);

        var help = new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Text = "One argument per line. Command-line arguments passed to the Launcher override this file."
        };
        var pathLabel = new Label
        {
            AutoEllipsis = true,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Text = $"Configuration: {configurationPath}"
        };

        var saveButton = new Button { AutoSize = true, Text = "Save" };
        saveButton.Click += (_, _) => SaveConfiguration();
        var cancelButton = new Button
        {
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
            Text = "Cancel"
        };
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(saveButton);

        var layout = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            RowCount = 10
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 7; row++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(_autoStart, 0, 0);
        layout.SetColumnSpan(_autoStart, 2);
        AddSetting(layout, "Server URLs", _urls, 1);
        AddSetting(layout, "Authentication", _authMode, 2);
        AddSetting(layout, "Working directory", workingDirectoryPanel, 3);
        AddSetting(layout, "Runtime retention (minutes)", _runtimeRetention, 4);
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Margin = new Padding(3, 8, 3, 3),
            Text = "Additional arguments"
        }, 0, 5);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, 5)!, 2);
        layout.Controls.Add(help, 0, 6);
        layout.SetColumnSpan(help, 2);
        layout.Controls.Add(_additionalArguments, 0, 7);
        layout.SetColumnSpan(_additionalArguments, 2);
        layout.Controls.Add(pathLabel, 0, 8);
        layout.SetColumnSpan(pathLabel, 2);
        layout.Controls.Add(buttons, 0, 9);
        layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);

        AcceptButton = saveButton;
        CancelButton = cancelButton;
    }

    internal LauncherConfiguration? Configuration { get; private set; }

    private static void AddSetting(
        TableLayoutPanel layout,
        string label,
        Control control,
        int row)
    {
        layout.Controls.Add(new Label
        {
            Anchor = AnchorStyles.Left,
            AutoSize = true,
            Margin = new Padding(3, 8, 12, 3),
            Text = label
        }, 0, row);
        control.Margin = new Padding(3, 4, 3, 4);
        layout.Controls.Add(control, 1, row);
    }

    private void BrowseWorkingDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select the starting directory for new Server Shell sessions",
            ShowNewFolderButton = true
        };
        if (Directory.Exists(_workingDirectory.Text))
        {
            dialog.SelectedPath = _workingDirectory.Text;
        }
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _workingDirectory.Text = dialog.SelectedPath;
        }
    }

    private void SaveConfiguration()
    {
        try
        {
            var additionalArguments = _additionalArguments.Lines
                .Select(argument => argument.Trim())
                .Where(argument => argument.Length > 0)
                .ToArray();
            Configuration = new LauncherConfiguration
            {
                AutoStart = _autoStart.Checked,
                Server = new LauncherServerConfiguration
                {
                    Urls = _urls.Text,
                    AuthMode = _authMode.SelectedItem as string ?? string.Empty,
                    WorkingDirectory = string.IsNullOrWhiteSpace(_workingDirectory.Text)
                        ? null
                        : _workingDirectory.Text,
                    RuntimeRetentionMinutes = (double)_runtimeRetention.Value,
                    AdditionalArguments = additionalArguments
                }
            }.Normalize();
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (LauncherConfigurationException exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }
}

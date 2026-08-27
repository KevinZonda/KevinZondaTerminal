using KevinZonda.Terminal.Server.UserAuth;

namespace KevinZonda.Terminal.Server.Launcher;

internal sealed class CredentialManagementForm : Form
{
    private readonly CredentialManager _manager;
    private readonly ListView _credentials = new()
    {
        Dock = DockStyle.Fill,
        FullRowSelect = true,
        HideSelection = false,
        MultiSelect = false,
        View = View.Details
    };
    private readonly Button _addPassword = new() { AutoSize = true, Text = "Add password..." };
    private readonly Button _generatePassword = new() { AutoSize = true, Text = "Generate random password..." };
    private readonly Button _delete = new() { AutoSize = true, Text = "Delete" };
    private readonly Label _status = new()
    {
        AutoEllipsis = true,
        AutoSize = true,
        ForeColor = SystemColors.GrayText
    };

    private ServerAuthConfiguration _configuration = new();
    private bool _loaded;
    private bool _operationInProgress;

    internal CredentialManagementForm(string configurationPath)
    {
        _manager = new CredentialManager(configurationPath);
        Text = "KTerm Credential Management";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(600, 400);
        Size = new Size(720, 480);
        ShowInTaskbar = true;

        _credentials.Columns.Add("Credential", 180);
        _credentials.Columns.Add("Hash fingerprint", 220);
        _credentials.SelectedIndexChanged += (_, _) => UpdateControls();
        _addPassword.Click += async (_, _) => await AddPasswordAsync();
        _generatePassword.Click += async (_, _) => await GeneratePasswordAsync();
        _delete.Click += async (_, _) => await DeleteCredentialAsync();

        var closeButton = new Button
        {
            AutoSize = true,
            DialogResult = DialogResult.OK,
            Text = "Close"
        };
        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true
        };
        actions.Controls.Add(_addPassword);
        actions.Controls.Add(_generatePassword);
        actions.Controls.Add(_delete);
        actions.Controls.Add(closeButton);

        var layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            RowCount = 5
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Text = "Allowed passwords"
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            AutoEllipsis = true,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 4, 3, 10),
            Text = $"Authentication file: {_manager.ConfigurationPath}"
        }, 0, 1);
        layout.Controls.Add(_credentials, 0, 2);
        layout.Controls.Add(_status, 0, 3);
        layout.Controls.Add(actions, 0, 4);
        Controls.Add(layout);

        AcceptButton = closeButton;
        CancelButton = closeButton;
        Shown += async (_, _) => await ReloadAsync();
        UpdateControls();
    }

    internal bool CredentialsChanged { get; private set; }

    internal int CredentialCount => _configuration.AllowedHash.Length;

    private async Task ReloadAsync()
    {
        await RunOperationAsync(async () =>
        {
            _configuration = await _manager.LoadAsync();
            _loaded = true;
            RenderCredentials();
        });
    }

    private async Task AddPasswordAsync()
    {
        using var passwordForm = new PasswordEntryForm();
        if (passwordForm.ShowDialog(this) != DialogResult.OK || passwordForm.Password is null)
        {
            return;
        }

        await RunOperationAsync(async () =>
        {
            _configuration = await _manager.AddPasswordAsync(passwordForm.Password);
            CredentialsChanged = true;
            RenderCredentials();
            _status.Text = "Password added. Restart the running Server to apply this change.";
        });
    }

    private async Task GeneratePasswordAsync()
    {
        var password = CredentialManager.GenerateRandomPassword();
        var added = false;
        await RunOperationAsync(async () =>
        {
            _configuration = await _manager.AddPasswordAsync(password);
            CredentialsChanged = true;
            added = true;
            RenderCredentials();
            _status.Text = "Random password added. Restart the running Server to apply this change.";
        });
        if (added)
        {
            using var generatedPasswordForm = new GeneratedPasswordForm(password);
            generatedPasswordForm.ShowDialog(this);
        }
    }

    private async Task DeleteCredentialAsync()
    {
        if (_credentials.SelectedItems.Count != 1 ||
            _credentials.SelectedItems[0].Tag is not CredentialEntry entry)
        {
            return;
        }

        var lastCredential = _configuration.AllowedHash.Length == 1;
        var warning = lastCredential
            ? "\n\nThis is the last credential. In auto mode the Server will fall back to no password; " +
              "in required mode the Server will fail to start."
            : string.Empty;
        if (MessageBox.Show(
                this,
                $"Delete credential {entry.Fingerprint}?{warning}",
                Text,
                MessageBoxButtons.YesNo,
                lastCredential ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        await RunOperationAsync(async () =>
        {
            _configuration = await _manager.DeleteAsync(entry.Hash);
            CredentialsChanged = true;
            RenderCredentials();
            _status.Text = lastCredential
                ? "Last credential deleted. Authentication behavior depends on the configured auth mode."
                : "Credential deleted. Restart the running Server to apply this change.";
        });
    }

    private async Task RunOperationAsync(Func<Task> operation)
    {
        if (_operationInProgress)
        {
            return;
        }

        _operationInProgress = true;
        UseWaitCursor = true;
        UpdateControls();
        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            _status.Text = "Credential operation failed.";
        }
        finally
        {
            _operationInProgress = false;
            UseWaitCursor = false;
            UpdateControls();
        }
    }

    private void RenderCredentials()
    {
        _credentials.BeginUpdate();
        try
        {
            _credentials.Items.Clear();
            var entries = CredentialManager.GetEntries(_configuration);
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                var item = new ListViewItem($"Credential {index + 1}")
                {
                    Tag = entry
                };
                item.SubItems.Add(entry.Fingerprint);
                _credentials.Items.Add(item);
            }
            _status.Text = entries.Count == 0
                ? "No allowed passwords are configured."
                : $"{entries.Count} of {ServerAuthConfiguration.MaximumAllowedHashes} credentials configured.";
        }
        finally
        {
            _credentials.EndUpdate();
        }
    }

    private void UpdateControls()
    {
        var canEdit = _loaded && !_operationInProgress;
        var hasCapacity = _configuration.AllowedHash.Length <
            ServerAuthConfiguration.MaximumAllowedHashes;
        _credentials.Enabled = canEdit;
        _addPassword.Enabled = canEdit && hasCapacity;
        _generatePassword.Enabled = canEdit && hasCapacity;
        _delete.Enabled = canEdit && _credentials.SelectedItems.Count == 1;
    }
}

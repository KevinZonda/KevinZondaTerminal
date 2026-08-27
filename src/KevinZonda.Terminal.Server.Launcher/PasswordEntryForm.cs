namespace KevinZonda.Terminal.Server.Launcher;

internal sealed class PasswordEntryForm : Form
{
    private readonly TextBox _password = new()
    {
        Dock = DockStyle.Fill,
        MaxLength = 4096,
        UseSystemPasswordChar = true
    };
    private readonly TextBox _confirmation = new()
    {
        Dock = DockStyle.Fill,
        MaxLength = 4096,
        UseSystemPasswordChar = true
    };

    internal PasswordEntryForm()
    {
        Text = "Add Credential";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(500, 210);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var showPassword = new CheckBox { AutoSize = true, Text = "Show password" };
        showPassword.CheckedChanged += (_, _) =>
        {
            _password.UseSystemPasswordChar = !showPassword.Checked;
            _confirmation.UseSystemPasswordChar = !showPassword.Checked;
        };
        var addButton = new Button { AutoSize = true, Text = "Add" };
        addButton.Click += (_, _) => AcceptPassword();
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
        buttons.Controls.Add(addButton);

        var layout = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            RowCount = 4
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        AddField(layout, "Password", _password, 0);
        AddField(layout, "Confirm password", _confirmation, 1);
        layout.Controls.Add(showPassword, 1, 2);
        layout.Controls.Add(buttons, 1, 3);
        Controls.Add(layout);

        AcceptButton = addButton;
        CancelButton = cancelButton;
    }

    internal string? Password { get; private set; }

    protected override void OnShown(EventArgs eventArgs)
    {
        base.OnShown(eventArgs);
        _password.Focus();
    }

    private void AcceptPassword()
    {
        if (_password.Text.Length == 0)
        {
            ShowWarning("The password cannot be empty.");
            return;
        }
        if (!string.Equals(_password.Text, _confirmation.Text, StringComparison.Ordinal))
        {
            ShowWarning("The passwords do not match.");
            return;
        }

        Password = _password.Text;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void ShowWarning(string message) => MessageBox.Show(
        this,
        message,
        Text,
        MessageBoxButtons.OK,
        MessageBoxIcon.Warning);

    private static void AddField(TableLayoutPanel layout, string label, Control field, int row)
    {
        layout.Controls.Add(new Label
        {
            Anchor = AnchorStyles.Left,
            AutoSize = true,
            Margin = new Padding(3, 8, 12, 3),
            Text = label
        }, 0, row);
        field.Margin = new Padding(3, 4, 3, 4);
        layout.Controls.Add(field, 1, row);
    }
}

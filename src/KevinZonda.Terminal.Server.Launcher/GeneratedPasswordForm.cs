using System.Runtime.InteropServices;

namespace KevinZonda.Terminal.Server.Launcher;

internal sealed class GeneratedPasswordForm : Form
{
    internal GeneratedPasswordForm(string password)
    {
        Text = "Generated Credential";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 190);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var passwordBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, 10),
            ReadOnly = true,
            Text = password
        };
        var copyButton = new Button { AutoSize = true, Text = "Copy" };
        copyButton.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(passwordBox.Text);
                copyButton.Text = "Copied";
            }
            catch (ExternalException exception)
            {
                MessageBox.Show(
                    this,
                    exception.Message,
                    Text,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        };
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
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        actions.Controls.Add(closeButton);
        actions.Controls.Add(copyButton);

        var layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            RowCount = 4
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "This password has been added to the Server. Copy it now; it cannot be recovered later."
        }, 0, 0);
        layout.Controls.Add(passwordBox, 0, 1);
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 8, 3, 3),
            Text = "The password is shown only in this window."
        }, 0, 2);
        layout.Controls.Add(actions, 0, 3);
        Controls.Add(layout);

        AcceptButton = closeButton;
        CancelButton = closeButton;
        Shown += (_, _) =>
        {
            passwordBox.SelectAll();
            passwordBox.Focus();
        };
    }
}

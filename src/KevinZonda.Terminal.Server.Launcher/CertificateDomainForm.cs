namespace KevinZonda.Terminal.Server.Launcher;

internal sealed class CertificateDomainForm : Form
{
    private readonly TextBox _domain = new()
    {
        Dock = DockStyle.Fill,
        Text = "kterm-backend.example.com"
    };
    private readonly TextBox _countryOrRegion = new() { Dock = DockStyle.Fill, MaxLength = 2 };
    private readonly TextBox _stateOrProvince = new() { Dock = DockStyle.Fill };
    private readonly TextBox _locality = new() { Dock = DockStyle.Fill };
    private readonly TextBox _organization = new() { Dock = DockStyle.Fill };
    private readonly TextBox _organizationalUnit = new() { Dock = DockStyle.Fill };

    internal CertificateDomainForm()
    {
        Text = "Generate Self-Signed Certificate";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(580, 390);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var saveButton = new Button { AutoSize = true, Text = "Generate" };
        saveButton.Click += (_, _) => AcceptDomain();
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
            RowCount = 9
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 6; row++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        AddField(layout, "Certificate domain", _domain, 0);
        AddField(layout, "Country/region (C)", _countryOrRegion, 1);
        AddField(layout, "State/province (ST)", _stateOrProvince, 2);
        AddField(layout, "Locality (L)", _locality, 3);
        AddField(layout, "Organization (O)", _organization, 4);
        AddField(layout, "Organizational unit (OU)", _organizationalUnit, 5);
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 6, 3, 3),
            Text = "Country/region is an optional two-letter code. Other subject fields are optional."
        }, 1, 6);
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 6, 3, 3),
            Text = "The certificate also includes localhost, 127.0.0.1, and ::1."
        }, 1, 7);
        layout.Controls.Add(buttons, 1, 8);
        Controls.Add(layout);

        AcceptButton = saveButton;
        CancelButton = cancelButton;
    }

    internal string? Domain { get; private set; }

    internal CertificateSubjectInformation? SubjectInformation { get; private set; }

    protected override void OnShown(EventArgs eventArgs)
    {
        base.OnShown(eventArgs);
        _domain.SelectAll();
        _domain.Focus();
    }

    private void AcceptDomain()
    {
        try
        {
            Domain = SelfSignedCertificateGenerator.GetOutputPaths(_domain.Text).Domain;
            SubjectInformation = new CertificateSubjectInformation(
                _countryOrRegion.Text,
                _stateOrProvince.Text,
                _locality.Text,
                _organization.Text,
                _organizationalUnit.Text);
            SelfSignedCertificateGenerator.ValidateSubjectInformation(SubjectInformation);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (CertificateGenerationException exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private static void AddField(
        TableLayoutPanel layout,
        string label,
        Control field,
        int row)
    {
        layout.Controls.Add(new Label
        {
            Anchor = AnchorStyles.Left,
            AutoSize = true,
            Margin = new Padding(3, 7, 12, 3),
            Text = label
        }, 0, row);
        layout.Controls.Add(field, 1, row);
    }
}

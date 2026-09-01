using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using KevinZonda.Terminal.Interop;

namespace KevinZonda.Terminal;

internal sealed class AboutForm : Form
{
    private const string RepositoryUrl = "https://github.com/KevinZonda/KevinZondaTerminal";

    private static readonly Color SurfaceColor = Color.FromArgb(23, 27, 34);
    private static readonly Color TextColor = Color.FromArgb(216, 222, 233);
    private static readonly Color DimTextColor = Color.FromArgb(170, 179, 192);
    private static readonly Color AccentColor = Color.FromArgb(136, 192, 208);

    private readonly List<Font> _ownedFonts = [];
    private Image? _logoImage;

    internal AboutForm()
    {
        Text = "About KevinZonda Terminal";
        BackColor = SurfaceColor;
        ForeColor = TextColor;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;

        var logo = new PictureBox
        {
            Image = DefaultFormIcon(),
            Size = new Size(48, 48),
            SizeMode = PictureBoxSizeMode.CenterImage,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 0, 0, 14)
        };
        _logoImage = logo.Image;

        var nameLabel = new Label
        {
            Text = "KevinZonda Terminal",
            Font = OwnedFont(15f, FontStyle.Bold),
            AutoSize = true,
            Anchor = AnchorStyles.None
        };

        var versionLabel = new Label
        {
            Text = $"Version {VersionString}",
            ForeColor = DimTextColor,
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 2, 0, 12)
        };

        var descriptionLabel = new Label
        {
            Text = "A terminal for Windows with workspaces, tabs and split panes.",
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 12)
        };

        var repositoryLink = new LinkLabel
        {
            Text = "GitHub: KevinZonda/KevinZondaTerminal",
            AutoSize = true,
            Dock = DockStyle.Fill,
            LinkColor = AccentColor,
            ActiveLinkColor = AccentColor,
            VisitedLinkColor = AccentColor,
            LinkBehavior = LinkBehavior.HoverUnderline,
            Margin = new Padding(0, 0, 0, 12)
        };
        repositoryLink.LinkClicked += (_, _) => OpenRepository();

        var commitLabel = new Label
        {
            Text = $"Commit {CommitHash}",
            ForeColor = DimTextColor,
            Font = OwnedFont(Font.Size * 0.85f, FontStyle.Regular),
            AutoSize = true,
            Dock = DockStyle.Fill
        };

        var okButton = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 0, 6, 0)
        };

        var okRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0)
        };
        okRow.Controls.Add(okButton);

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            Padding = new Padding(24, 20, 24, 14),
            ColumnCount = 1
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 412f));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(logo, 0, 0);
        layout.Controls.Add(nameLabel, 0, 1);
        layout.Controls.Add(versionLabel, 0, 2);
        layout.Controls.Add(descriptionLabel, 0, 3);
        layout.Controls.Add(repositoryLink, 0, 4);
        layout.Controls.Add(commitLabel, 0, 5);
        layout.Controls.Add(okRow, 0, 6);

        Controls.Add(layout);
        AcceptButton = okButton;
        CancelButton = okButton;
    }

    // "0.1.0+<commit>" — the SDK appends the source revision when building
    // inside a git checkout.
    private static string InformationalVersion =>
        typeof(AboutForm).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? typeof(AboutForm).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private static string VersionString
    {
        get
        {
            var plusIndex = InformationalVersion.IndexOf('+', StringComparison.Ordinal);
            return plusIndex < 0 ? InformationalVersion : InformationalVersion[..plusIndex];
        }
    }

    private static string CommitHash
    {
        get
        {
            var plusIndex = InformationalVersion.IndexOf('+', StringComparison.Ordinal);
            if (plusIndex < 0)
            {
                return "unknown";
            }

            var hash = InformationalVersion[(plusIndex + 1)..];
            return hash.Length > 7 ? hash[..7] : hash;
        }
    }

    // The classic WinForms default icon: grab it from a throwaway Form.
    private static Image DefaultFormIcon()
    {
        using var holder = new Form();
        return (holder.Icon ?? SystemIcons.Application).ToBitmap();
    }

    private Font OwnedFont(float size, FontStyle style)
    {
        var font = new Font(Font.FontFamily, size, style);
        _ownedFonts.Add(font);
        return font;
    }

    private void OpenRepository()
    {
        try
        {
            Process.Start(new ProcessStartInfo(RepositoryUrl) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            // No handler for https URLs; nothing useful to surface here.
        }
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        var enabled = 1;
        NativeMethods.DwmSetWindowAttribute(
            Handle,
            NativeMethods.DwmUseImmersiveDarkMode,
            ref enabled,
            Marshal.SizeOf<int>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            foreach (var font in _ownedFonts)
            {
                font.Dispose();
            }

            _ownedFonts.Clear();
            _logoImage?.Dispose();
            _logoImage = null;
        }
    }
}

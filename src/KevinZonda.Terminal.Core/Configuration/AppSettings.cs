namespace KevinZonda.Terminal.Configuration;

internal sealed record AppSettings
{
    internal const string DefaultFontFamily =
        "Cascadia Mono, Cascadia Code, Menlo, Consolas, Microsoft YaHei, monospace";
    internal const double DefaultFontSize = 14;
    internal const double DefaultLineHeight = 1.12;

    public FontSettings Font { get; init; } = new();

    public ThemeSettings Theme { get; init; } = new();

    public CursorSettings Cursor { get; init; } = new();

    public BellSettings Bell { get; init; } = new();

    public IndicatorSettings Indicators { get; init; } = new();

    public WorkspaceBehaviorSettings Workspace { get; init; } = new();

    public ShellSettings Shell { get; init; } = new();

    public ConHostSettings ConHost { get; init; } = new();

    internal static AppSettings Normalize(AppSettings? settings)
    {
        var font = settings?.Font ?? new FontSettings();
        var family = font.Family?.Trim();
        if (string.IsNullOrEmpty(family) || family.Length > 256)
        {
            family = DefaultFontFamily;
        }

        var theme = TerminalThemeCatalog.Find(settings?.Theme?.Name);
        return new AppSettings
        {
            Font = new FontSettings
            {
                Family = family,
                Size = double.IsFinite(font.Size)
                    ? Math.Clamp(font.Size, 8, 72)
                    : DefaultFontSize,
                LineHeight = double.IsFinite(font.LineHeight)
                    ? Math.Clamp(font.LineHeight, 0.8, 2)
                    : DefaultLineHeight,
                EnableLigatures = font.EnableLigatures
            },
            Theme = new ThemeSettings
            {
                Name = theme.Name
            },
            Cursor = CursorSettings.Normalize(settings?.Cursor),
            Bell = BellSettings.Normalize(settings?.Bell),
            Indicators = IndicatorSettings.Normalize(settings?.Indicators),
            Workspace = WorkspaceBehaviorSettings.Normalize(settings?.Workspace),
            Shell = ShellSettings.Normalize(settings?.Shell),
            ConHost = ConHostSettings.Normalize(settings?.ConHost)
        };
    }
}

internal sealed record BellSettings
{
    internal const string NoneSound = "None";
    internal const string Tone880To660HzSound = "880-660Hz";
    internal const string NoVisualFeedback = "None";
    internal const string BriefVisualFeedback = "Briefly";
    internal const string UntilViewedVisualFeedback = "UntilViewed";

    public string Sound { get; init; } = Tone880To660HzSound;

    public string VisualFeedback { get; init; } = BriefVisualFeedback;

    internal static BellSettings Normalize(BellSettings? settings) => new()
    {
        Sound = settings?.Sound == NoneSound ? NoneSound : Tone880To660HzSound,
        VisualFeedback = settings?.VisualFeedback switch
        {
            NoVisualFeedback => NoVisualFeedback,
            UntilViewedVisualFeedback => UntilViewedVisualFeedback,
            _ => BriefVisualFeedback
        }
    };
}

internal sealed record WorkspaceBehaviorSettings
{
    internal const string CloseWorkspaceLastTabBehavior = "CloseWorkspace";
    internal const string OpenNewTabLastTabBehavior = "OpenNewTab";
    internal const string QuitApplicationLastWorkspaceBehavior = "QuitApplication";
    internal const string CreateWorkspaceLastWorkspaceBehavior = "CreateWorkspace";

    public string LastTabClosedBehavior { get; init; } = OpenNewTabLastTabBehavior;

    public string LastWorkspaceClosedBehavior { get; init; } = CreateWorkspaceLastWorkspaceBehavior;

    internal static WorkspaceBehaviorSettings Normalize(WorkspaceBehaviorSettings? settings) => new()
    {
        LastTabClosedBehavior = settings?.LastTabClosedBehavior == CloseWorkspaceLastTabBehavior
            ? CloseWorkspaceLastTabBehavior
            : OpenNewTabLastTabBehavior,
        LastWorkspaceClosedBehavior =
            settings?.LastWorkspaceClosedBehavior == QuitApplicationLastWorkspaceBehavior
                ? QuitApplicationLastWorkspaceBehavior
                : CreateWorkspaceLastWorkspaceBehavior
    };
}

internal sealed record ConHostSettings
{
    // The enhanced OpenConsole build (tools/openconsole/OpenConsole.Enhanced.exe,
    // KTerm patch from docs/OpenCon.FixB.md) additionally repaints the viewport
    // when an application stays silent after a resize, restoring content that
    // xterm.js lost when it truncated narrowed lines.
    public bool EnhancedOpenConsole { get; init; }

    internal static ConHostSettings Normalize(ConHostSettings? settings) => new()
    {
        EnhancedOpenConsole = settings?.EnhancedOpenConsole ?? false
    };
}

internal sealed record FontSettings
{
    public string Family { get; init; } = AppSettings.DefaultFontFamily;

    public double Size { get; init; } = AppSettings.DefaultFontSize;

    public double LineHeight { get; init; } = AppSettings.DefaultLineHeight;

    public bool EnableLigatures { get; init; }
}

internal sealed record ThemeSettings
{
    public string Name { get; init; } = TerminalThemeCatalog.DefaultName;
}

internal sealed record CursorSettings
{
    internal const string BlockShape = "block";
    internal const string UnderlineShape = "underline";
    internal const string BarShape = "bar";

    public string Shape { get; init; } = BarShape;

    public bool Blink { get; init; } = true;

    internal static CursorSettings Normalize(CursorSettings? settings) => new()
    {
        Shape = settings?.Shape?.ToLowerInvariant() switch
        {
            BlockShape => BlockShape,
            UnderlineShape => UnderlineShape,
            _ => BarShape
        },
        Blink = settings?.Blink ?? true
    };
}

internal sealed record IndicatorSettings
{
    public bool ShowWorkspaceIndicator { get; init; } = true;

    public bool ShowRemainingUsage { get; init; }

    public bool AutoRenewKimiToken { get; init; }

    internal static IndicatorSettings Normalize(IndicatorSettings? settings) => new()
    {
        ShowWorkspaceIndicator = settings?.ShowWorkspaceIndicator ?? true,
        ShowRemainingUsage = settings?.ShowRemainingUsage ?? false,
        AutoRenewKimiToken = settings?.AutoRenewKimiToken ?? false
    };
}

internal sealed record ShellSettings
{
    internal const string KeepTabExitBehavior = "KeepTab";
    internal const string CloseTabExitBehavior = "CloseTab";

    public string Profile { get; init; } = ShellProfileCatalog.AutoId;

    public string? Executable { get; init; }

    public string? Arguments { get; init; }

    public string Msys2Environment { get; init; } = ShellProfileCatalog.DefaultMsys2Environment;

    public bool InheritWindowsPath { get; init; } = true;

    public string ExitBehavior { get; init; } = KeepTabExitBehavior;

    internal bool HasSameLaunchConfiguration(ShellSettings other) =>
        Profile == other.Profile &&
        Executable == other.Executable &&
        Arguments == other.Arguments &&
        Msys2Environment == other.Msys2Environment &&
        InheritWindowsPath == other.InheritWindowsPath;

    internal static ShellSettings Normalize(ShellSettings? settings)
    {
        var profile = ShellProfileCatalog.Find(settings?.Profile);
        var executable = NormalizeText(settings?.Executable, 1_024);
        var arguments = NormalizeText(settings?.Arguments, 4_096, preserveEmpty: true);
        return new ShellSettings
        {
            Profile = profile.Id,
            Executable = executable,
            Arguments = arguments,
            Msys2Environment = ShellProfileCatalog.NormalizeMsys2Environment(
                settings?.Msys2Environment),
            InheritWindowsPath = settings?.InheritWindowsPath ?? true,
            ExitBehavior = settings?.ExitBehavior == CloseTabExitBehavior
                ? CloseTabExitBehavior
                : KeepTabExitBehavior
        };
    }

    private static string? NormalizeText(string? value, int maximumLength, bool preserveEmpty = false)
    {
        if (value is null)
        {
            return null;
        }

        value = value.Trim();
        if (value.Length > maximumLength)
        {
            return null;
        }

        return value.Length == 0 && !preserveEmpty ? null : value;
    }
}

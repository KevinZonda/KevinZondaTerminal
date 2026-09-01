namespace KevinZonda.Terminal.Configuration;

internal sealed record AppSettings
{
    internal const string DefaultFontFamily =
        "Cascadia Mono, Cascadia Code, Consolas, Microsoft YaHei, monospace";
    internal const double DefaultFontSize = 14;
    internal const double DefaultLineHeight = 1.12;

    public FontSettings Font { get; init; } = new();

    public ThemeSettings Theme { get; init; } = new();

    public CursorSettings Cursor { get; init; } = new();

    public IndicatorSettings Indicators { get; init; } = new();

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
            Indicators = IndicatorSettings.Normalize(settings?.Indicators),
            Shell = ShellSettings.Normalize(settings?.Shell),
            ConHost = ConHostSettings.Normalize(settings?.ConHost)
        };
    }
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

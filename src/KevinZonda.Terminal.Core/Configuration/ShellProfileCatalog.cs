namespace KevinZonda.Terminal.Configuration;

internal sealed record ShellProfileDefinition(
    string Id,
    string DisplayName,
    string DefaultArguments);

internal sealed record Msys2EnvironmentDefinition(
    string Id,
    string DisplayName)
{
    public override string ToString() => DisplayName;
}

internal sealed record ShellLaunchSpec(
    string DisplayName,
    string ExecutablePath,
    string Arguments,
    IReadOnlyDictionary<string, string>? Environment = null,
    IReadOnlySet<string>? RemovedEnvironmentVariables = null);

internal static class ShellProfileCatalog
{
    internal const string AutoId = "auto";
    internal const string PowerShell7Id = "powershell-7";
    internal const string WindowsPowerShellId = "windows-powershell";
    internal const string CommandPromptId = "command-prompt";
    internal const string Msys2Id = "msys2";
    internal const string GitBashId = "git-bash";
    internal const string CustomId = "custom";
    internal const string NoMsys2Environment = "none";
    internal const string DefaultMsys2Environment = "UCRT64";

    internal static IReadOnlyList<Msys2EnvironmentDefinition> Msys2Environments { get; } =
    [
        new(NoMsys2Environment, "None (do not set MSYSTEM)"),
        new(DefaultMsys2Environment, DefaultMsys2Environment),
        new("CLANG64", "CLANG64"),
        new("CLANGARM64", "CLANGARM64"),
        new("MINGW64", "MINGW64"),
        new("MSYS", "MSYS")
    ];

    internal static IReadOnlyList<ShellProfileDefinition> All { get; } =
    [
        new(AutoId, "Auto", string.Empty),
        new(PowerShell7Id, "PowerShell 7", string.Empty),
        new(WindowsPowerShellId, "Windows PowerShell", string.Empty),
        new(CommandPromptId, "Command Prompt", string.Empty),
        new(Msys2Id, "MSYS2", "-l -i"),
        new(GitBashId, "Git Bash", "--login -i"),
        new(CustomId, "Custom executable", string.Empty)
    ];

    internal static ShellProfileDefinition Find(string? id) =>
        All.FirstOrDefault(
            profile => string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? All[0];

    internal static Msys2EnvironmentDefinition FindMsys2Environment(string? environment) =>
        Msys2Environments.FirstOrDefault(
            candidate => string.Equals(candidate.Id, environment, StringComparison.OrdinalIgnoreCase))
        ?? Msys2Environments.First(candidate => candidate.Id == DefaultMsys2Environment);

    internal static string NormalizeMsys2Environment(string? environment) =>
        FindMsys2Environment(environment).Id;

    internal static ShellLaunchSpec Resolve(ShellSettings settings)
    {
        var normalized = ShellSettings.Normalize(settings);
        var profile = Find(normalized.Profile);
        return profile.Id switch
        {
            AutoId => ResolveAuto(),
            Msys2Id => CreateMsys2Zsh(profile, normalized),
            _ => Create(profile, normalized)
        };
    }

    private static ShellLaunchSpec CreateMsys2Zsh(
        ShellProfileDefinition profile,
        ShellSettings settings)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CHERE_INVOKING"] = "1"
        };
        IReadOnlySet<string>? removedEnvironmentVariables = null;
        if (settings.Msys2Environment == NoMsys2Environment)
        {
            removedEnvironmentVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "MSYSTEM"
            };
        }
        else
        {
            environment["MSYSTEM"] = settings.Msys2Environment;
        }

        if (settings.InheritWindowsPath)
        {
            environment["MSYS2_PATH_TYPE"] = "inherit";
        }

        return Create(profile, settings, environment, removedEnvironmentVariables);
    }

    private static ShellLaunchSpec ResolveAuto()
    {
        var candidates = new[]
        {
            (
                Find(PowerShell7Id),
                new[]
                {
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        "PowerShell",
                        "7",
                        "pwsh.exe"),
                    "pwsh.exe"
                }),
            (
                Find(WindowsPowerShellId),
                new[]
                {
                    Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                    "powershell.exe"
                }),
            (
                Find(CommandPromptId),
                new[] { Environment.GetEnvironmentVariable("COMSPEC") ?? string.Empty, "cmd.exe" })
        };

        foreach (var (profile, executableCandidates) in candidates)
        {
            var executable = FindFirstExecutable(executableCandidates);
            if (executable is not null)
            {
                return new ShellLaunchSpec(
                    profile.DisplayName,
                    executable,
                    profile.DefaultArguments);
            }
        }

        throw new FileNotFoundException("No supported command shell was found.");
    }

    private static ShellLaunchSpec Create(
        ShellProfileDefinition profile,
        ShellSettings settings,
        IReadOnlyDictionary<string, string>? environment = null,
        IReadOnlySet<string>? removedEnvironmentVariables = null)
    {
        var executable = ResolveManualExecutable(settings.Executable);
        if (executable is null || !File.Exists(executable))
        {
            throw new FileNotFoundException($"Enter an existing executable path for {profile.DisplayName}.");
        }

        return new ShellLaunchSpec(
            profile.DisplayName,
            executable,
            settings.Arguments ?? profile.DefaultArguments,
            environment,
            removedEnvironmentVariables);
    }

    private static string? ResolveManualExecutable(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
        return Path.IsPathFullyQualified(expanded) ? Path.GetFullPath(expanded) : null;
    }

    private static string? FindFirstExecutable(IReadOnlyList<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            var resolved = ResolveCandidate(candidate);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return null;
    }

    private static string? ResolveCandidate(string candidate)
    {
        candidate = Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'));
        if (candidate.Length == 0)
        {
            return null;
        }

        if (Path.IsPathFullyQualified(candidate))
        {
            return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
        }

        return FindOnPath(candidate);
    }

    private static string? FindOnPath(string fileName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var pathEntry in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.GetFullPath(Path.Combine(pathEntry.Trim(), fileName));
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }

        return null;
    }
}

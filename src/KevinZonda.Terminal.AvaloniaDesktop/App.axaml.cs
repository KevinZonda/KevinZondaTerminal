using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace KevinZonda.Terminal.AvaloniaDesktop;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(ResolveWorkingDirectory(desktop.Args));
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static string ResolveWorkingDirectory(string[]? args)
    {
        if (args is { Length: > 0 })
        {
            for (var index = 0; index < args.Length; index++)
            {
                if (args[index] == "--working-directory" &&
                    index + 1 < args.Length &&
                    Directory.Exists(args[index + 1]))
                {
                    return Path.GetFullPath(args[index + 1]);
                }
            }

            var positional = args.FirstOrDefault(argument =>
                !argument.StartsWith("-", StringComparison.Ordinal) && Directory.Exists(argument));
            if (positional is not null)
            {
                return Path.GetFullPath(positional);
            }
        }

        var currentDirectory = Environment.CurrentDirectory;
        return Directory.Exists(currentDirectory) &&
               !string.Equals(currentDirectory, Path.GetPathRoot(currentDirectory), StringComparison.Ordinal)
            ? currentDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
}

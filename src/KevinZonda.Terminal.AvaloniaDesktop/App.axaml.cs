using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace KevinZonda.Terminal.AvaloniaDesktop;

public sealed class App : Application
{
    private AboutWindow? _aboutWindow;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(ResolveWorkingDirectory(desktop.Args));
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async void HandleAboutClick(object? sender, EventArgs eventArgs)
    {
        if (_aboutWindow is { IsVisible: true } existing)
        {
            existing.Activate();
            return;
        }

        var dialog = new AboutWindow();
        _aboutWindow = dialog;
        try
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
                {
                    MainWindow: { IsVisible: true } owner
                })
            {
                await dialog.ShowDialog(owner);
            }
            else
            {
                dialog.Show();
                dialog.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_aboutWindow, dialog))
                    {
                        _aboutWindow = null;
                    }
                };
                return;
            }
        }
        finally
        {
            if (!dialog.IsVisible && ReferenceEquals(_aboutWindow, dialog))
            {
                _aboutWindow = null;
            }
        }
    }

    private async void HandleSettingsClick(object? sender, EventArgs eventArgs)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
            {
                MainWindow: MainWindow mainWindow
            })
        {
            await mainWindow.OpenSettingsAsync();
        }
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

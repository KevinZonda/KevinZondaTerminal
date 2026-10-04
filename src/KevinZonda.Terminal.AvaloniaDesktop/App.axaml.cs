using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace KevinZonda.Terminal.AvaloniaDesktop;

public sealed class App : Application
{
    private AboutWindow? _aboutWindow;
    private IActivatableLifetime? _activatableLifetime;
    private MainWindow? _initialWindow;
    private IClassicDesktopStyleApplicationLifetime? _desktopLifetime;
    private string? _initialWorkingDirectory;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktopLifetime = desktop;
            _initialWorkingDirectory = ResolveWorkingDirectory(desktop.Args);
            _activatableLifetime = this.TryGetFeature<IActivatableLifetime>();
            if (_activatableLifetime is not null)
            {
                _activatableLifetime.Activated += HandleActivated;
            }

            Dispatcher.UIThread.Post(ShowInitialWindow, DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void HandleActivated(object? sender, ActivatedEventArgs eventArgs)
    {
        if (eventArgs is not FileActivatedEventArgs fileActivation ||
            _desktopLifetime is null)
        {
            return;
        }

        var workingDirectories = ResolveActivationWorkingDirectories(
            fileActivation.Files.Select(item => item.Path));
        if (workingDirectories.Count == 0)
        {
            return;
        }

        var index = 0;
        if (_initialWindow is null)
        {
            ShowInitialWindow(workingDirectories[0]);
            index = 1;
        }

        for (; index < workingDirectories.Count; index++)
        {
            var window = new MainWindow(workingDirectories[index]);
            window.Show();
            window.Activate();
        }
    }

    private void ShowInitialWindow() =>
        ShowInitialWindow(_initialWorkingDirectory ??
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private void ShowInitialWindow(string workingDirectory)
    {
        if (_initialWindow is not null || _desktopLifetime is null)
        {
            return;
        }

        _initialWindow = new MainWindow(workingDirectory);
        _desktopLifetime.MainWindow = _initialWindow;
        _initialWindow.Show();
        _initialWindow.Activate();
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

    internal static IReadOnlyList<string> ResolveActivationWorkingDirectories(
        IEnumerable<Uri> itemUris)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var uri in itemUris)
        {
            if (!uri.IsFile)
            {
                continue;
            }

            var path = uri.LocalPath;
            string? workingDirectory = null;
            if (Directory.Exists(path))
            {
                workingDirectory = Path.GetFullPath(path);
            }
            else if (File.Exists(path))
            {
                workingDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
            }

            if (workingDirectory is not null && seen.Add(workingDirectory))
            {
                results.Add(workingDirectory);
            }
        }

        return results;
    }
}

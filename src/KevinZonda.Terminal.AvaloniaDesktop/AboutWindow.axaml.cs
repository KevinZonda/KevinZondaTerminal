using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace KevinZonda.Terminal.AvaloniaDesktop;

internal sealed partial class AboutWindow : Window
{
    private const string RepositoryUrl = "https://github.com/KevinZonda/KevinZondaTerminal";

    internal AboutWindow()
    {
        InitializeComponent();
        this.FindControl<TextBlock>("VersionText")!.Text = $"Version {VersionString}";
        this.FindControl<TextBlock>("RuntimeText")!.Text =
            $"{RuntimeInformation.OSDescription.Trim()} · " +
            $"{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()} · " +
            RuntimeInformation.FrameworkDescription;

        var commit = CommitHash;
        var commitText = this.FindControl<TextBlock>("CommitText")!;
        commitText.Text = commit is null ? string.Empty : $"Commit {commit}";
        commitText.IsVisible = commit is not null;
    }

    internal static string InformationalVersion =>
        typeof(AboutWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? typeof(AboutWindow).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    internal static string VersionString
    {
        get
        {
            var plusIndex = InformationalVersion.IndexOf('+', StringComparison.Ordinal);
            return plusIndex < 0 ? InformationalVersion : InformationalVersion[..plusIndex];
        }
    }

    internal static string? CommitHash
    {
        get
        {
            var plusIndex = InformationalVersion.IndexOf('+', StringComparison.Ordinal);
            if (plusIndex < 0 || plusIndex == InformationalVersion.Length - 1)
            {
                return null;
            }

            var hash = InformationalVersion[(plusIndex + 1)..];
            return hash.Length > 7 ? hash[..7] : hash;
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void HandleCloseClick(object? sender, RoutedEventArgs eventArgs) => Close();

    private void HandleRepositoryClick(object? sender, RoutedEventArgs eventArgs)
    {
        try
        {
            Process.Start(new ProcessStartInfo(RepositoryUrl) { UseShellExecute = true });
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            // The platform has no registered HTTPS handler.
        }
    }
}

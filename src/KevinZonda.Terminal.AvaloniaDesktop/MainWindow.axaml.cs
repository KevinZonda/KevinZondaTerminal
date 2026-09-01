using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace KevinZonda.Terminal.AvaloniaDesktop;

public sealed partial class MainWindow : Window
{
    private readonly string _workingDirectory;
    private NativeWebView _webView = null!;
    private LocalAssetServer? _assetServer;
    private UnixTerminalSessionManager? _sessions;
    private AgentUsageStatusService? _agentUsage;
    private SystemMetricsService? _systemMetrics;
    private AvaloniaWebViewBridge? _bridge;
    private bool _initialized;
    private bool _canClose;
    private bool _cleanupStarted;

    public MainWindow()
        : this(Environment.CurrentDirectory)
    {
    }

    public MainWindow(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
        InitializeComponent();
        Opened += HandleOpened;
        Closing += HandleClosing;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _webView = this.FindControl<NativeWebView>("WebView")
            ?? throw new InvalidOperationException("The NativeWebView control was not created.");
    }

    private async void HandleOpened(object? sender, EventArgs eventArgs)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        try
        {
            _assetServer = await LocalAssetServer.StartAsync();
            _sessions = new UnixTerminalSessionManager(_workingDirectory);
            _agentUsage = new AgentUsageStatusService(
                _sessions,
                new DesktopSettingsStore().Load());
            _systemMetrics = new SystemMetricsService();
            _systemMetrics.Start();
            _bridge = new AvaloniaWebViewBridge(
                _webView,
                _sessions,
                _agentUsage,
                _systemMetrics,
                this,
                _workingDirectory);
            _agentUsage.Start();
            _webView.Source = _assetServer.StartPage;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            await ShowStartupErrorAsync(exception);
            await CleanupAsync();
            _canClose = true;
            Close();
        }
    }

    private async void HandleClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        if (_canClose)
        {
            return;
        }

        eventArgs.Cancel = true;
        await CleanupAsync();
        _canClose = true;
        Close();
    }

    private async Task CleanupAsync()
    {
        if (_cleanupStarted)
        {
            return;
        }

        _cleanupStarted = true;
        _bridge?.Dispose();
        if (_agentUsage is not null)
        {
            await _agentUsage.DisposeAsync();
        }
        if (_systemMetrics is not null)
        {
            await _systemMetrics.DisposeAsync();
        }
        if (_sessions is not null)
        {
            await _sessions.DisposeAsync();
        }
        if (_assetServer is not null)
        {
            await _assetServer.DisposeAsync();
        }
    }

    private async Task ShowStartupErrorAsync(Exception exception)
    {
        var dialog = new Window
        {
            Title = "KevinZonda Terminal startup error",
            Width = 560,
            Height = 220,
            CanResize = false,
            Content = new TextBlock
            {
                Margin = new Avalonia.Thickness(24),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Text = $"KevinZonda Terminal could not start.\n\n{exception.Message}"
            }
        };
        await dialog.ShowDialog(this);
    }

    internal async Task ShowSettingsPlaceholderAsync()
    {
        var dialog = new Window
        {
            Title = "KevinZonda Terminal Settings",
            Width = 480,
            Height = 180,
            CanResize = false,
            Content = new TextBlock
            {
                Margin = new Avalonia.Thickness(24),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Text = "The Avalonia terminal already shares ~/.kterm/config.json. " +
                       "The native settings editor will be added after the terminal workflow is stable."
            }
        };
        await dialog.ShowDialog(this);
    }
}

namespace KevinZonda.Terminal.Server.Launcher;

internal sealed class ServerLauncherContext : ApplicationContext
{
    private readonly Control _dispatcher = new();
    private readonly LauncherLogBuffer _logs = new();
    private readonly ServerProcessHost _server;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _startItem = new("Start");
    private readonly ToolStripMenuItem _stopItem = new("Stop");
    private readonly ToolStripMenuItem _logsItem = new("Logs");
    private readonly ToolStripMenuItem _exitItem = new("Exit");
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly System.Windows.Forms.Timer _autoStartTimer = new();
    private LogsForm? _logsForm;
    private bool _operationInProgress;
    private bool _exitRequested;
    private int _disposed;

    internal ServerLauncherContext(string[] serverArguments)
    {
        _dispatcher.CreateControl();
        _server = new ServerProcessHost(
            ServerExecutableLocator.Find(),
            serverArguments,
            _logs);
        _server.StateChanged += HandleServerStateChanged;
        _server.UnexpectedExit += HandleUnexpectedExit;

        _startItem.Click += async (_, _) => await RunOperationAsync(_server.StartAsync);
        _stopItem.Click += async (_, _) => await RunOperationAsync(_server.StopAsync);
        _logsItem.Click += (_, _) => ShowLogs();
        _exitItem.Click += async (_, _) => await ExitAsync();
        _menu.Items.Add(_startItem);
        _menu.Items.Add(_stopItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_logsItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_exitItem);

        _icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)
            ?? (Icon)SystemIcons.Application.Clone();
        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Icon = _icon,
            Text = "KTerm Server - Stopped",
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ShowLogs();
        UpdateMenuState();

        _logs.Add(LauncherLogSource.System, "KTerm Server Launcher started.");
        _autoStartTimer.Interval = 1;
        _autoStartTimer.Tick += async (_, _) =>
        {
            _autoStartTimer.Stop();
            await RunOperationAsync(_server.StartAsync);
        };
        _autoStartTimer.Start();
    }

    protected override void ExitThreadCore()
    {
        DisposeResources();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeResources();
        }
        base.Dispose(disposing);
    }

    private async Task RunOperationAsync(Func<Task> operation)
    {
        if (_operationInProgress || _exitRequested)
        {
            return;
        }

        _operationInProgress = true;
        UpdateMenuState();
        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            _logs.Add(LauncherLogSource.StandardError, exception.ToString());
            _notifyIcon.ShowBalloonTip(
                5000,
                "KTerm Server Launcher",
                exception.Message,
                ToolTipIcon.Error);
        }
        finally
        {
            _operationInProgress = false;
            UpdateMenuState();
        }
    }

    private async Task ExitAsync()
    {
        if (_exitRequested || _operationInProgress)
        {
            return;
        }

        _exitRequested = true;
        _operationInProgress = true;
        UpdateMenuState();
        try
        {
            await _server.StopAsync();
        }
        catch (Exception exception)
        {
            _logs.Add(LauncherLogSource.StandardError, exception.ToString());
        }
        finally
        {
            _operationInProgress = false;
            _logsForm?.CloseForExit();
            ExitThread();
        }
    }

    private void ShowLogs()
    {
        if (_logsForm is null || _logsForm.IsDisposed)
        {
            _logsForm = new LogsForm(_logs);
        }
        if (!_logsForm.Visible)
        {
            _logsForm.Show();
        }
        if (_logsForm.WindowState == FormWindowState.Minimized)
        {
            _logsForm.WindowState = FormWindowState.Normal;
        }
        _logsForm.Activate();
    }

    private void HandleServerStateChanged() => Dispatch(UpdateMenuState);

    private void HandleUnexpectedExit(int exitCode) => Dispatch(() =>
    {
        UpdateMenuState();
        _notifyIcon.ShowBalloonTip(
            5000,
            "KTerm Server stopped",
            $"kterm-server exited unexpectedly with code {exitCode}.",
            ToolTipIcon.Warning);
    });

    private void Dispatch(Action action)
    {
        if (Volatile.Read(ref _disposed) != 0 || _dispatcher.IsDisposed)
        {
            return;
        }
        if (_dispatcher.InvokeRequired)
        {
            try
            {
                _dispatcher.BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
            }
        }
        else
        {
            action();
        }
    }

    private void UpdateMenuState()
    {
        var running = _server.IsRunning;
        _startItem.Enabled = !_exitRequested && !_operationInProgress && !running;
        _stopItem.Enabled = !_exitRequested && !_operationInProgress && running;
        _logsItem.Enabled = !_exitRequested;
        _exitItem.Enabled = !_exitRequested && !_operationInProgress;
        _notifyIcon.Text = running
            ? "KTerm Server - Running"
            : "KTerm Server - Stopped";
    }

    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _autoStartTimer.Stop();
        _autoStartTimer.Dispose();
        _server.StateChanged -= HandleServerStateChanged;
        _server.UnexpectedExit -= HandleUnexpectedExit;
        _server.Dispose();
        _logsForm?.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
        _dispatcher.Dispose();
    }
}

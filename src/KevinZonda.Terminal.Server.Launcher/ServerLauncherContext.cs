namespace KevinZonda.Terminal.Server.Launcher;

internal sealed class ServerLauncherContext : ApplicationContext
{
    private readonly Control _dispatcher = new();
    private readonly LauncherLogBuffer _logs = new();
    private readonly LauncherConfigurationStore _configurationStore;
    private readonly string[] _commandLineServerArguments;
    private readonly string _serverWorkingDirectory;
    private readonly ServerProcessHost _server;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _startItem = new("Start");
    private readonly ToolStripMenuItem _stopItem = new("Stop");
    private readonly ToolStripMenuItem _settingsItem = new("Settings...");
    private readonly ToolStripMenuItem _credentialsItem = new("Credential Management...");
    private readonly ToolStripMenuItem _logsItem = new("Logs");
    private readonly ToolStripMenuItem _exitItem = new("Exit");
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly System.Windows.Forms.Timer _autoStartTimer = new();
    private LogsForm? _logsForm;
    private LauncherConfiguration _configuration;
    private bool _configurationValid;
    private bool _operationInProgress;
    private bool _exitRequested;
    private int _disposed;

    internal ServerLauncherContext(
        LauncherConfigurationStore configurationStore,
        LauncherConfiguration configuration,
        string[] commandLineServerArguments,
        string? configurationError)
    {
        _configurationStore = configurationStore;
        _configuration = configuration;
        _commandLineServerArguments = [.. commandLineServerArguments];
        _configurationValid = configurationError is null;
        _dispatcher.CreateControl();
        var serverExecutable = ServerExecutableLocator.Find();
        _serverWorkingDirectory = Path.GetDirectoryName(serverExecutable) ?? AppContext.BaseDirectory;
        _server = new ServerProcessHost(
            serverExecutable,
            configuration.BuildServerArguments(_commandLineServerArguments),
            _logs);
        _server.StateChanged += HandleServerStateChanged;
        _server.UnexpectedExit += HandleUnexpectedExit;

        _startItem.Click += async (_, _) => await RunOperationAsync(_server.StartAsync);
        _stopItem.Click += async (_, _) => await RunOperationAsync(_server.StopAsync);
        _settingsItem.Click += async (_, _) => await ShowSettingsAsync();
        _credentialsItem.Click += async (_, _) => await ShowCredentialManagementAsync();
        _logsItem.Click += (_, _) => ShowLogs();
        _exitItem.Click += async (_, _) => await ExitAsync();
        _menu.Items.Add(_startItem);
        _menu.Items.Add(_stopItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_settingsItem);
        _menu.Items.Add(_credentialsItem);
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
        _logs.Add(
            LauncherLogSource.System,
            $"Launcher configuration: {_configurationStore.ConfigurationPath}");
        _autoStartTimer.Interval = 1;
        _autoStartTimer.Tick += async (_, _) =>
        {
            _autoStartTimer.Stop();
            await RunOperationAsync(_server.StartAsync);
        };
        if (configurationError is not null)
        {
            _logs.Add(LauncherLogSource.StandardError, configurationError);
            _notifyIcon.ShowBalloonTip(
                5000,
                "Invalid Launcher configuration",
                "Open Settings to repair server_launcher.json.",
                ToolTipIcon.Error);
        }
        else if (_configuration.AutoStart)
        {
            _autoStartTimer.Start();
        }
        else
        {
            _logs.Add(LauncherLogSource.System, "Server auto-start is disabled.");
        }
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
            ReportError(exception);
        }
        finally
        {
            _operationInProgress = false;
            UpdateMenuState();
        }
    }

    private async Task ShowSettingsAsync()
    {
        if (_operationInProgress || _exitRequested)
        {
            return;
        }

        using var form = new SettingsForm(
            _configuration,
            _configurationStore.ConfigurationPath);
        if (form.ShowDialog() != DialogResult.OK || form.Configuration is null)
        {
            return;
        }

        _operationInProgress = true;
        UpdateMenuState();
        try
        {
            var configuration = form.Configuration.Normalize();
            var serverArguments = configuration.BuildServerArguments(_commandLineServerArguments);
            var serverSettingsChanged = !_configuration
                .BuildServerArguments(_commandLineServerArguments)
                .SequenceEqual(serverArguments, StringComparer.Ordinal);
            _configurationStore.Save(configuration);
            _configuration = configuration;
            _configurationValid = true;
            _server.UpdateArguments(serverArguments);
            _logs.Add(
                LauncherLogSource.System,
                $"Saved Launcher configuration: {_configurationStore.ConfigurationPath}");

            if (serverSettingsChanged && _server.IsRunning && MessageBox.Show(
                    "The new Server settings have been saved. Restart Server now?",
                    "KTerm Server Launcher",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) == DialogResult.Yes)
            {
                await _server.StopAsync();
                await _server.StartAsync();
            }
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
        finally
        {
            _operationInProgress = false;
            UpdateMenuState();
        }
    }

    private async Task ShowCredentialManagementAsync()
    {
        if (_operationInProgress || _exitRequested)
        {
            return;
        }

        _operationInProgress = true;
        UpdateMenuState();
        try
        {
            var serverArguments = _configuration.BuildServerArguments(_commandLineServerArguments);
            var authenticationPath = ServerAuthFileArgumentResolver.Resolve(
                serverArguments,
                _serverWorkingDirectory);
            using var form = new CredentialManagementForm(authenticationPath);
            form.ShowDialog();
            if (!form.CredentialsChanged)
            {
                return;
            }

            _logs.Add(
                LauncherLogSource.System,
                $"Updated Server credentials: {authenticationPath}");
            if (!_server.IsRunning || MessageBox.Show(
                    form.CredentialCount == 0
                        ? "The credential file is now empty. Restarting in auto mode will disable password " +
                          "authentication; required mode will fail to start. Restart Server now?"
                        : "Server credentials have changed. Restart Server now to apply them?",
                    "KTerm Server Launcher",
                    MessageBoxButtons.YesNo,
                    form.CredentialCount == 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question) !=
                DialogResult.Yes)
            {
                return;
            }

            await _server.StopAsync();
            await _server.StartAsync();
        }
        catch (Exception exception)
        {
            ReportError(exception);
            MessageBox.Show(
                exception.Message,
                "KTerm Credential Management",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
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
        _startItem.Enabled = _configurationValid &&
            !_exitRequested &&
            !_operationInProgress &&
            !running;
        _stopItem.Enabled = !_exitRequested && !_operationInProgress && running;
        _settingsItem.Enabled = !_exitRequested && !_operationInProgress;
        _credentialsItem.Enabled = !_exitRequested && !_operationInProgress;
        _logsItem.Enabled = !_exitRequested;
        _exitItem.Enabled = !_exitRequested && !_operationInProgress;
        _notifyIcon.Text = running
            ? "KTerm Server - Running"
            : "KTerm Server - Stopped";
    }

    private void ReportError(Exception exception)
    {
        _logs.Add(LauncherLogSource.StandardError, exception.ToString());
        _notifyIcon.ShowBalloonTip(
            5000,
            "KTerm Server Launcher",
            exception.Message,
            ToolTipIcon.Error);
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

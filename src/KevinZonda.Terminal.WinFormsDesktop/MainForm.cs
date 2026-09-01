using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using KevinZonda.AgentUsageMonitor;
using KevinZonda.Terminal.Configuration;
using KevinZonda.Terminal.ConPty;
using KevinZonda.Terminal.Hosting;
using KevinZonda.Terminal.Interop;
using KevinZonda.Terminal.Messaging;
using KevinZonda.SystemMetrics;
using KevinZonda.Terminal.Terminal;
using KevinZonda.Terminal.Web;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace KevinZonda.Terminal;

internal sealed class MainForm : Form
{
    private const string AppHostName = "com.kevinzonda.terminal";
    private const int SystemCommandSettings = 0x1000;
    private const int SystemCommandAbout = 0x1001;
    private const long PreviousKeyStateMask = 1L << 30;
    private static readonly Color FrameColor = Color.FromArgb(23, 27, 34);
    private static readonly Color FrameBorderColor = Color.FromArgb(48, 56, 69);
    private static readonly Color FrameTextColor = Color.FromArgb(216, 222, 233);

    private readonly WebView2 _webView;
    private readonly TerminalSessionManager _sessions;
    private readonly IAgentUsageMonitorService _agentUsage;
    private readonly ISystemMetricsService _systemMetrics;
    private readonly SettingsStore _settingsStore = new();
    private readonly string _startingDirectory;
    private AppSettings _settings;
    private WebViewBridge? _bridge;
    private CoreWebView2Environment? _webViewEnvironment;
    private bool _initialized;
    private bool _allowClose;
    private bool _settingsOpen;

    internal MainForm(string startingDirectory)
    {
        _startingDirectory = startingDirectory;
        _settings = _settingsStore.Load();
        _sessions = new TerminalSessionManager(
            _settings,
            startingDirectory,
            ConPtyTerminalSessionFactory.Instance);
        _agentUsage = new AgentUsageMonitorService(
            _sessions.GetSessionProcessIds,
            CreateAgentUsageOptions(_settings));
        _systemMetrics = new SystemMetricsService();
        Text = "KevinZonda Terminal";
        BackColor = Color.FromArgb(12, 15, 20);
        ClientSize = new Size(1100, 720);
        MinimumSize = new Size(640, 400);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = BackColor
        };
        Controls.Add(_webView);

        Shown += HandleShown;
        FormClosing += HandleFormClosing;
    }

    private static AgentUsageMonitorOptions CreateAgentUsageOptions(AppSettings settings) => new()
    {
        AutoRenewKimiToken = settings.Indicators.AutoRenewKimiToken
    };

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        ApplyDwmFrameColors();
        AddCustomSystemMenuItems();
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == NativeMethods.WmSysCommand)
        {
            var command = message.WParam.ToInt64();
            if (command == SystemCommandSettings)
            {
                BeginInvoke((Action)ShowSettings);
                message.Result = IntPtr.Zero;
                return;
            }

            if (command == SystemCommandAbout)
            {
                BeginInvoke((Action)ShowAbout);
                message.Result = IntPtr.Zero;
                return;
            }
        }

        base.WndProc(ref message);
    }

    private async void HandleShown(object? sender, EventArgs eventArgs)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        try
        {
            _sessions.Prewarm(80, 24);
            await InitializeWebView();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"KevinZonda Terminal could not initialize WebView2.\n\n{exception.Message}",
                "KevinZonda Terminal startup error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Close();
        }
    }

    private async Task InitializeWebView()
    {
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KTerm",
            "WebView2");
        Directory.CreateDirectory(userDataFolder);

        var environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userDataFolder);
        _webViewEnvironment = environment;
        await _webView.EnsureCoreWebView2Async(environment);

        var core = _webView.CoreWebView2;
        core.AddWebResourceRequestedFilter(
            $"https://{AppHostName}/*",
            CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += HandleWebResourceRequested;

        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
#if DEBUG
        core.Settings.AreDevToolsEnabled = true;
#else
        core.Settings.AreDevToolsEnabled = false;
#endif

        core.NavigationStarting += HandleNavigationStarting;
#if DEBUG
        core.NavigationCompleted += HandleDebugNavigationCompleted;
#endif
        core.NewWindowRequested += HandleNewWindowRequested;
        core.ProcessFailed += HandleProcessFailed;
        core.DocumentTitleChanged += (_, _) =>
            Text = string.IsNullOrEmpty(core.DocumentTitle) ? "KevinZonda Terminal" : core.DocumentTitle;
        _bridge = new WebViewBridge(
            _webView,
            _sessions,
            _agentUsage,
            _systemMetrics,
            ShowSettings,
            LaunchNewInstance,
            Close,
            OpenExternal,
            SaveFontSize,
            _settings);
        _agentUsage.Start();
        _systemMetrics.Start();

        core.Navigate($"https://{AppHostName}/index.html");
    }

    private void HandleWebResourceRequested(
        object? sender,
        CoreWebView2WebResourceRequestedEventArgs eventArgs)
    {
        var environment = _webViewEnvironment;
        if (environment is null ||
            !Uri.TryCreate(eventArgs.Request.Uri, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, AppHostName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!string.Equals(eventArgs.Request.Method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            eventArgs.Response = environment.CreateWebResourceResponse(
                Stream.Null,
                405,
                "Method Not Allowed",
                "Allow: GET");
            return;
        }

        var requestPath = Uri.UnescapeDataString(uri.AbsolutePath);
        if (!EmbeddedWebAssets.TryOpen(requestPath, out var content, out var contentType) ||
            content is null)
        {
            eventArgs.Response = environment.CreateWebResourceResponse(
                Stream.Null,
                404,
                "Not Found",
                "Content-Type: text/plain; charset=utf-8");
            return;
        }

        var cacheControl = EmbeddedWebAssets.IsImmutable(requestPath)
            ? "public, max-age=31536000, immutable"
            : "no-store";
        var headers =
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {content.Length}\r\n" +
            $"Cache-Control: {cacheControl}\r\n" +
            "X-Content-Type-Options: nosniff";
        eventArgs.Response = environment.CreateWebResourceResponse(content, 200, "OK", headers);
    }

#if DEBUG
    private async void HandleDebugNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs eventArgs)
    {
        if (!eventArgs.IsSuccess || Environment.GetEnvironmentVariable("KTERM_SMOKE_TEST") != "1")
        {
            return;
        }

        _webView.CoreWebView2.NavigationCompleted -= HandleDebugNavigationCompleted;
        await Task.Delay(4_000);
        await DispatchDebugEnvironmentProbe();
        await DispatchDebugShortcut("KeyT", "t", 0x54);
        await Task.Delay(500);
        await DispatchDebugShortcut("Backslash", "\\", 0xDC);
        await Task.Delay(500);
        await DispatchDebugShortcut("Minus", "-", 0xBD);
        await Task.Delay(500);
        await DispatchDebugClick(250, 250);
        await Task.Delay(250);
        await DispatchDebugShortcut("Minus", "-", 0xBD);
        await Task.Delay(750);
        await DispatchDebugClick(80, 18, "middle");
        await Task.Delay(500);
        await DispatchDebugShortcut("KeyT", "t", 0x54);
        await Task.Delay(500);
        await DispatchDebugClick(80, 18);
        await Task.Delay(250);
        await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync(
            "Input.insertText",
            JsonSerializer.Serialize(new { text = "echo KTERM_SMOKE" }));
        await DispatchDebugShortcut("Enter", "\r", 0x0D, modifiers: 0);
        await Task.Delay(500);
        WriteDebugSmokeCompletion();
    }

    private async Task DispatchDebugEnvironmentProbe()
    {
        var outputPath = Environment.GetEnvironmentVariable("KTERM_SMOKE_OUTPUT");
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        var shell = ShellProfileCatalog.Resolve(_settings.Shell);
        var executableName = Path.GetFileNameWithoutExtension(shell.ExecutablePath);
        var command = executableName.ToLowerInvariant() switch
        {
            "powershell" or "pwsh" =>
                $"@($env:TERM,$env:COLORTERM) | Set-Content -LiteralPath '{outputPath.Replace("'", "''", StringComparison.Ordinal)}'",
            "cmd" =>
                $"(echo %TERM%& echo %COLORTERM%) > \"{outputPath.Replace("\"", "\"\"", StringComparison.Ordinal)}\"",
            _ =>
                $"printf '%s\\n%s\\n' \"$TERM\" \"$COLORTERM\" > '{ToMsysPath(outputPath).Replace("'", "'\\''", StringComparison.Ordinal)}'"
        };
        for (var attempt = 0; attempt < 3 && !File.Exists(outputPath); attempt++)
        {
            await DispatchDebugClick(250, 250);
            await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync(
                "Input.insertText",
                JsonSerializer.Serialize(new { text = command }));
            await DispatchDebugShortcut("Enter", "\r", 0x0D, modifiers: 0);
            await Task.Delay(750);
        }
    }

    private static string ToMsysPath(string path)
    {
        var normalized = Path.GetFullPath(path).Replace('\\', '/');
        return normalized.Length >= 3 && normalized[1] == ':' && normalized[2] == '/'
            ? $"/{char.ToLowerInvariant(normalized[0])}{normalized[2..]}"
            : normalized;
    }

    private static void WriteDebugSmokeCompletion()
    {
        var completionPath = Environment.GetEnvironmentVariable("KTERM_SMOKE_COMPLETE");
        if (!string.IsNullOrWhiteSpace(completionPath))
        {
            File.WriteAllText(completionPath, "complete");
        }
    }

    private async Task DispatchDebugShortcut(string code, string key, int virtualKey, int modifiers = 1)
    {
        var arguments = JsonSerializer.Serialize(new
        {
            type = "keyDown",
            modifiers,
            windowsVirtualKeyCode = virtualKey,
            nativeVirtualKeyCode = virtualKey,
            code,
            key
        });
        await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", arguments);

        arguments = JsonSerializer.Serialize(new
        {
            type = "keyUp",
            modifiers,
            windowsVirtualKeyCode = virtualKey,
            nativeVirtualKeyCode = virtualKey,
            code,
            key
        });
        await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", arguments);
    }

    private async Task DispatchDebugClick(int x, int y, string button = "left")
    {
        var arguments = JsonSerializer.Serialize(new
        {
            type = "mousePressed",
            x,
            y,
            button,
            clickCount = 1
        });
        await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", arguments);

        arguments = JsonSerializer.Serialize(new
        {
            type = "mouseReleased",
            x,
            y,
            button,
            clickCount = 1
        });
        await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", arguments);
    }
#endif

    private void HandleNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs eventArgs)
    {
        if (Uri.TryCreate(eventArgs.Uri, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Host, AppHostName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        eventArgs.Cancel = true;
        OpenExternal(eventArgs.Uri);
    }

    private void HandleNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        OpenExternal(eventArgs.Uri);
    }

    private void HandleProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs eventArgs) =>
        _bridge?.NotifyRuntimeFailure(eventArgs.ProcessFailedKind.ToString());

    private void ApplyDwmFrameColors()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        var enabled = 1;
        var roundedCorners = 2;
        var borderColor = ColorTranslator.ToWin32(FrameBorderColor);
        var captionColor = ColorTranslator.ToWin32(FrameColor);
        var textColor = ColorTranslator.ToWin32(FrameTextColor);
        var valueSize = Marshal.SizeOf<int>();

        NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DwmUseImmersiveDarkMode, ref enabled, valueSize);
        NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DwmWindowCornerPreference, ref roundedCorners, valueSize);
        NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DwmBorderColor, ref borderColor, valueSize);
        NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DwmCaptionColor, ref captionColor, valueSize);
        NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DwmTextColor, ref textColor, valueSize);
    }

    private void AddCustomSystemMenuItems()
    {
        var systemMenu = NativeMethods.GetSystemMenu(Handle, false);
        if (systemMenu == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.AppendMenuW(
            systemMenu,
            NativeMethods.MenuFlagSeparator,
            0,
            null);
        NativeMethods.AppendMenuW(
            systemMenu,
            NativeMethods.MenuFlagString,
            SystemCommandSettings,
            "Settings...\tAlt+S");
        NativeMethods.AppendMenuW(
            systemMenu,
            NativeMethods.MenuFlagString,
            SystemCommandAbout,
            "About");
    }

    private void SetSettingsSystemMenuEnabled(bool enabled)
    {
        if (!IsHandleCreated) return;

        var systemMenu = NativeMethods.GetSystemMenu(Handle, false);
        if (systemMenu == IntPtr.Zero) return;

        var state = NativeMethods.MenuFlagByCommand |
            (enabled ? NativeMethods.MenuFlagEnabled : NativeMethods.MenuFlagGrayed);
        NativeMethods.EnableMenuItem(systemMenu, SystemCommandSettings, state);
        NativeMethods.DrawMenuBar(Handle);
    }

    private static void OpenExternal(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(parsed.AbsoluteUri)
        {
            UseShellExecute = true
        });
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if ((keyData & Keys.Modifiers) == (Keys.Control | Keys.Shift) &&
            (keyData & Keys.KeyCode) == Keys.N)
        {
            if ((message.LParam.ToInt64() & PreviousKeyStateMask) == 0)
            {
                LaunchNewInstance();
            }
            return true;
        }

        if ((keyData & Keys.Modifiers) == Keys.Alt)
        {
            if ((keyData & Keys.KeyCode) == Keys.S)
            {
                ShowSettings();
                return true;
            }

            var command = (keyData & Keys.KeyCode) switch
            {
                Keys.T => "newTab",
                Keys.B => "toggleSidebar",
                Keys.N => "newWorkspace",
                Keys.Oem5 => "splitColumns",
                Keys.OemMinus => "splitRows",
                _ => null
            };

            if (command is not null && _bridge is not null)
            {
                _bridge.SendWorkspaceCommand(command);
                return true;
            }
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    private void LaunchNewInstance()
    {
        try
        {
            var startInfo = SelfProcessLauncher.CreateStartInfo(
                _startingDirectory,
                [_startingDirectory]);
            if (Process.Start(startInfo) is null)
            {
                throw new InvalidOperationException("Windows did not start the new KevinZonda Terminal instance.");
            }
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or
                FileNotFoundException or
                InvalidOperationException)
        {
            MessageBox.Show(
                this,
                $"Unable to open a new KevinZonda Terminal window.\n\n{exception.Message}",
                "KevinZonda Terminal",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void ShowAbout()
    {
        using var aboutForm = new AboutForm();
        aboutForm.ShowDialog(this);
    }

    private async void ShowSettings()
    {
        if (_settingsOpen || IsDisposed)
        {
            return;
        }

        _settingsOpen = true;
        SetSettingsSystemMenuEnabled(false);
        try
        {
            using var settingsForm = new SettingsForm(_settings);
            if (settingsForm.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            try
            {
                _settings = await _settingsStore.SaveAsync(settingsForm.Settings);
                await _sessions.UpdateSettingsAsync(_settings);
                _agentUsage.UpdateOptions(CreateAgentUsageOptions(_settings));
                // Do not eagerly launch a hidden shell after saving settings.
                // Closing the window while an MSYS2 prewarm is still loading
                // can surface a zsh DLL-initialization error during Job cleanup.
                // The next requested tab will start with the new settings on demand.
                _bridge?.SendSettingsChanged(_settings);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(
                    this,
                    $"KevinZonda Terminal could not save settings.\n\n{exception.Message}",
                    "KevinZonda Terminal settings",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
        finally
        {
            _settingsOpen = false;
            SetSettingsSystemMenuEnabled(true);
        }
    }

    private async Task<AppSettings> SaveFontSize(double size)
    {
        _settings = await _settingsStore.SaveAsync(_settings with
        {
            Font = _settings.Font with { Size = size }
        });
        return _settings;
    }

    private async void HandleFormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        if (_allowClose)
        {
            return;
        }

        eventArgs.Cancel = true;
        Enabled = false;
        _bridge?.Dispose();
        _bridge = null;
        await _agentUsage.DisposeAsync();
        await _systemMetrics.DisposeAsync();
        await _sessions.DisposeAsync();

        if (_webView.CoreWebView2 is not null)
        {
            _webView.CoreWebView2.NavigationStarting -= HandleNavigationStarting;
            _webView.CoreWebView2.WebResourceRequested -= HandleWebResourceRequested;
#if DEBUG
            _webView.CoreWebView2.NavigationCompleted -= HandleDebugNavigationCompleted;
#endif
            _webView.CoreWebView2.NewWindowRequested -= HandleNewWindowRequested;
            _webView.CoreWebView2.ProcessFailed -= HandleProcessFailed;
        }

        _webView.Dispose();
        _allowClose = true;
        Close();
    }
}

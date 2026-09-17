using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using System.Diagnostics;
using KevinZonda.AgentUsageMonitor;
using KevinZonda.AgentUsageMonitor.KimiCode;
using KevinZonda.Terminal.Configuration;

namespace KevinZonda.Terminal.AvaloniaDesktop;

internal sealed partial class SettingsWindow : Window
{
    private readonly ComboBox _fontFamily;
    private readonly NumericUpDown _fontSize;
    private readonly NumericUpDown _lineHeight;
    private readonly CheckBox _ligatures;
    private readonly ComboBox _cursorShape;
    private readonly CheckBox _cursorBlink;
    private readonly TextBlock _fontPreview;
    private readonly ComboBox _theme;
    private readonly Border _themePreview;
    private readonly TextBlock _themePreviewPrompt;
    private readonly TextBlock _themePreviewOutput;
    private readonly Border _themePreviewCursor;
    private readonly Border _themePreviewSelection;
    private readonly TextBlock _themePreviewSelectionText;
    private readonly ItemsControl _themePreviewPalette;
    private readonly CheckBox _workspaceIndicator;
    private readonly CheckBox _remainingUsage;
    private readonly ComboBox _kimiMode;
    private readonly ComboBox _kimiRegion;
    private readonly Button _kimiLogin;
    private readonly Button _kimiLogout;
    private readonly Button _kimiLoginCancel;
    private readonly TextBlock _kimiStatus;
    private readonly TextBox _kimiVerification;
    private readonly HttpClient _oauthHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly KimiOAuthManager _kimiOAuth;
    private CancellationTokenSource? _kimiLoginCancellation;
    private bool _closed;
    private readonly ComboBox _bellSound;
    private readonly ComboBox _tabVisualFeedback;
    private readonly ComboBox _workspaceVisualFeedback;
    private readonly ComboBox _lastTabClosedBehavior;
    private readonly ComboBox _lastWorkspaceClosedBehavior;
    private readonly ComboBox _shellExitBehavior;
    private AppSettings _basisSettings;
    private bool _applyingValues;

    internal SettingsWindow(AppSettings settings)
    {
        _kimiOAuth = new KimiOAuthManager(_oauthHttp);
        _basisSettings = AppSettings.Normalize(settings);
        AvaloniaXamlLoader.Load(this);
        _fontFamily = Find<ComboBox>("FontFamilyBox");
        _fontSize = Find<NumericUpDown>("FontSizeBox");
        _lineHeight = Find<NumericUpDown>("LineHeightBox");
        _ligatures = Find<CheckBox>("LigaturesBox");
        _cursorShape = Find<ComboBox>("CursorShapeBox");
        _cursorBlink = Find<CheckBox>("CursorBlinkBox");
        _fontPreview = Find<TextBlock>("FontPreviewText");
        _theme = Find<ComboBox>("ThemeBox");
        _themePreview = Find<Border>("ThemePreviewBorder");
        _themePreviewPrompt = Find<TextBlock>("ThemePreviewPrompt");
        _themePreviewOutput = Find<TextBlock>("ThemePreviewOutput");
        _themePreviewCursor = Find<Border>("ThemePreviewCursor");
        _themePreviewSelection = Find<Border>("ThemePreviewSelection");
        _themePreviewSelectionText = Find<TextBlock>("ThemePreviewSelectionText");
        _themePreviewPalette = Find<ItemsControl>("ThemePreviewPalette");
        _workspaceIndicator = Find<CheckBox>("WorkspaceIndicatorBox");
        _remainingUsage = Find<CheckBox>("RemainingUsageBox");
        _kimiMode = Find<ComboBox>("KimiUsageModeBox");
        _kimiRegion = Find<ComboBox>("KimiOAuthRegionBox");
        _kimiLogin = Find<Button>("KimiLoginButton");
        _kimiLogout = Find<Button>("KimiLogoutButton");
        _kimiLoginCancel = Find<Button>("KimiLoginCancelButton");
        _kimiStatus = Find<TextBlock>("KimiOAuthStatusText");
        _kimiVerification = Find<TextBox>("KimiVerificationText");
        _bellSound = Find<ComboBox>("BellSoundBox");
        _tabVisualFeedback = Find<ComboBox>("TabVisualFeedbackBox");
        _workspaceVisualFeedback = Find<ComboBox>("WorkspaceVisualFeedbackBox");
        _lastTabClosedBehavior = Find<ComboBox>("LastTabClosedBehaviorBox");
        _lastWorkspaceClosedBehavior = Find<ComboBox>("LastWorkspaceClosedBehaviorBox");
        _shellExitBehavior = Find<ComboBox>("ShellExitBehaviorBox");

        _fontFamily.ItemsSource = FontManager.Current.SystemFonts
            .Select(font => font.Name)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        _cursorShape.ItemsSource = new[] { "Block", "Underline", "Bar" };
        _theme.ItemsSource = TerminalThemeCatalog.All.Select(theme => theme.Name).ToArray();
        _bellSound.ItemsSource = new[] { "None", "880–660 Hz" };
        _tabVisualFeedback.ItemsSource = new[] { "None", "Briefly", "Until viewed" };
        _workspaceVisualFeedback.ItemsSource =
            new[] { "None", "Until workspace viewed", "Until all bells viewed" };
        _lastTabClosedBehavior.ItemsSource = new[] { "Close the workspace", "Open a new tab" };
        _lastWorkspaceClosedBehavior.ItemsSource =
            new[] { "Quit KevinZonda Terminal", "Create a new workspace" };
        _shellExitBehavior.ItemsSource = new[] { "Keep tab open", "Close tab" };
        _kimiMode.ItemsSource = new[] { "Passive - use Kimi Code CLI", "Active - independent OAuth" };
        _kimiRegion.ItemsSource = new[] { "Mainland China", "Global" };
        _kimiMode.SelectionChanged += (_, _) => { _kimiLoginCancellation?.Cancel(); UpdateKimiControls(); };
        _kimiLogin.Click += async (_, _) => await LoginKimiAsync();
        _kimiLogout.Click += async (_, _) =>
        {
            try { await _kimiOAuth.LogoutAsync(); await RefreshKimiStatusAsync(); }
            catch (Exception) { if (!_closed) _kimiStatus.Text = "Unable to log out. Try again."; }
        };
        _kimiLoginCancel.Click += (_, _) => _kimiLoginCancellation?.Cancel();
        Opened += async (_, _) => await RefreshKimiStatusAsync();
        Closed += (_, _) => { _closed = true; _kimiLoginCancellation?.Cancel(); _oauthHttp.Dispose(); };

        _fontFamily.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.Property == ComboBox.TextProperty)
            {
                UpdateFontPreview();
            }
        };
        _fontSize.ValueChanged += (_, _) => UpdateFontPreview();
        _theme.SelectionChanged += (_, _) => UpdateThemePreview();

        ApplyValues(settings);
    }

    internal AppSettings Settings => AppSettings.Normalize(_basisSettings with
    {
        Font = new FontSettings
        {
            Family = _fontFamily.Text ?? AppSettings.DefaultFontFamily,
            Size = decimal.ToDouble(_fontSize.Value ?? 14),
            LineHeight = decimal.ToDouble(_lineHeight.Value ?? 1.12m),
            EnableLigatures = _ligatures.IsChecked == true
        },
        Theme = new ThemeSettings
        {
            Name = _theme.SelectedItem as string ?? TerminalThemeCatalog.DefaultName
        },
        Cursor = new CursorSettings
        {
            Shape = (_cursorShape.SelectedItem as string)?.ToLowerInvariant() ?? "bar",
            Blink = _cursorBlink.IsChecked == true
        },
        Indicators = new IndicatorSettings
        {
            ShowWorkspaceIndicator = _workspaceIndicator.IsChecked == true,
            ShowRemainingUsage = _remainingUsage.IsChecked == true,
            KimiUsageMode = _kimiMode.SelectedIndex == 1 ? "Active" : "Passive",
            KimiOAuthRegion = _kimiRegion.SelectedIndex == 1 ? "global" : "mainland-cn"
        },
        Bell = new BellSettings
        {
            Sound = _bellSound.SelectedIndex == 0
                ? BellSettings.NoneSound
                : BellSettings.Tone880To660HzSound,
            TabVisualFeedback = _tabVisualFeedback.SelectedIndex switch
            {
                0 => BellSettings.NoVisualFeedback,
                2 => BellSettings.UntilViewedTabVisualFeedback,
                _ => BellSettings.BriefTabVisualFeedback
            },
            WorkspaceVisualFeedback = _workspaceVisualFeedback.SelectedIndex switch
            {
                0 => BellSettings.NoVisualFeedback,
                1 => BellSettings.UntilWorkspaceViewedVisualFeedback,
                _ => BellSettings.UntilAllBellsViewedVisualFeedback
            }
        },
        Workspace = new WorkspaceBehaviorSettings
        {
            LastTabClosedBehavior = _lastTabClosedBehavior.SelectedIndex == 0
                ? WorkspaceBehaviorSettings.CloseWorkspaceLastTabBehavior
                : WorkspaceBehaviorSettings.OpenNewTabLastTabBehavior,
            LastWorkspaceClosedBehavior = _lastWorkspaceClosedBehavior.SelectedIndex == 0
                ? WorkspaceBehaviorSettings.QuitApplicationLastWorkspaceBehavior
                : WorkspaceBehaviorSettings.CreateWorkspaceLastWorkspaceBehavior
        },
        Shell = _basisSettings.Shell with
        {
            ExitBehavior = _shellExitBehavior.SelectedIndex == 1 ? "CloseTab" : "KeepTab"
        }
    });

    private T Find<T>(string name) where T : Control =>
        this.FindControl<T>(name) ?? throw new InvalidOperationException(
            $"Settings control '{name}' was not created.");

    private void ApplyValues(AppSettings settings)
    {
        var normalized = AppSettings.Normalize(settings);
        _basisSettings = normalized;
        _applyingValues = true;
        try
        {
            _fontFamily.Text = normalized.Font.Family;
            _fontSize.Value = (decimal)normalized.Font.Size;
            _lineHeight.Value = (decimal)normalized.Font.LineHeight;
            _ligatures.IsChecked = normalized.Font.EnableLigatures;
            _cursorShape.SelectedItem = normalized.Cursor.Shape switch
            {
                "block" => "Block",
                "underline" => "Underline",
                _ => "Bar"
            };
            _cursorBlink.IsChecked = normalized.Cursor.Blink;
            _theme.SelectedItem = normalized.Theme.Name;
            _workspaceIndicator.IsChecked = normalized.Indicators.ShowWorkspaceIndicator;
            _remainingUsage.IsChecked = normalized.Indicators.ShowRemainingUsage;
            _kimiMode.SelectedIndex = normalized.Indicators.KimiUsageMode == "Active" ? 1 : 0;
            _kimiRegion.SelectedIndex = normalized.Indicators.KimiOAuthRegion == "global" ? 1 : 0;
            UpdateKimiControls();
            _bellSound.SelectedIndex = normalized.Bell.Sound == BellSettings.NoneSound ? 0 : 1;
            _tabVisualFeedback.SelectedIndex = normalized.Bell.TabVisualFeedback switch
            {
                BellSettings.NoVisualFeedback => 0,
                BellSettings.UntilViewedTabVisualFeedback => 2,
                _ => 1
            };
            _workspaceVisualFeedback.SelectedIndex = normalized.Bell.WorkspaceVisualFeedback switch
            {
                BellSettings.NoVisualFeedback => 0,
                BellSettings.UntilWorkspaceViewedVisualFeedback => 1,
                _ => 2
            };
            _lastTabClosedBehavior.SelectedIndex =
                normalized.Workspace.LastTabClosedBehavior ==
                    WorkspaceBehaviorSettings.CloseWorkspaceLastTabBehavior
                    ? 0
                    : 1;
            _lastWorkspaceClosedBehavior.SelectedIndex =
                normalized.Workspace.LastWorkspaceClosedBehavior ==
                    WorkspaceBehaviorSettings.QuitApplicationLastWorkspaceBehavior
                    ? 0
                    : 1;
            _shellExitBehavior.SelectedIndex = normalized.Shell.ExitBehavior == "CloseTab" ? 1 : 0;
        }
        finally
        {
            _applyingValues = false;
        }

        UpdateFontPreview();
        UpdateThemePreview();
    }

    private void UpdateFontPreview()
    {
        if (_applyingValues)
        {
            return;
        }

        var family = _fontFamily.Text?.Trim();
        if (!string.IsNullOrEmpty(family))
        {
            try
            {
                _fontPreview.FontFamily = new FontFamily(family);
            }
            catch (ArgumentException)
            {
                _fontPreview.FontFamily = FontManager.Current.DefaultFontFamily;
            }
        }

        _fontPreview.FontSize = decimal.ToDouble(_fontSize.Value ?? 14);
    }

    private void UpdateThemePreview()
    {
        if (_applyingValues)
        {
            return;
        }

        var preset = TerminalThemeCatalog.Find(_theme.SelectedItem as string);
        _themePreview.Background = Brush.Parse(preset.Background);
        _themePreviewPrompt.Foreground = Brush.Parse(preset.Foreground);
        _themePreviewOutput.Foreground = Brush.Parse(preset.Foreground);
        _themePreviewCursor.Background = Brush.Parse(preset.Cursor);
        _themePreviewSelection.Background = Brush.Parse(preset.SelectionBackground);
        _themePreviewSelectionText.Foreground = Brush.Parse(preset.Foreground);
        _themePreviewPalette.ItemsSource = preset.AnsiColors.Select(CreateColorSwatch).ToArray();
    }

    private static Border CreateColorSwatch(string color)
    {
        var swatch = new Border
        {
            Height = 18,
            Background = Brush.Parse(color)
        };
        ToolTip.SetTip(swatch, color);
        return swatch;
    }

    private void HandleRestoreDefaults(object? sender, RoutedEventArgs eventArgs) =>
        ApplyValues(new AppSettings());

    private void UpdateKimiControls()
    {
        var active = _kimiMode.SelectedIndex == 1;
        var busy = _kimiLoginCancellation is not null;
        _kimiRegion.IsEnabled = active && !busy;
        _kimiLogin.IsEnabled = active && !busy;
        _kimiLogout.IsEnabled = active && !busy;
        _kimiLoginCancel.IsEnabled = busy;
        _kimiVerification.IsVisible = active && !string.IsNullOrEmpty(_kimiVerification.Text);
    }

    private async Task RefreshKimiStatusAsync()
    {
        try
        {
            var status = await _kimiOAuth.GetStatusAsync();
            if (_closed) return;
            _kimiStatus.Text = status.IsLoggedIn
                ? $"Active authorization saved ({(status.Region == KimiOAuthRegion.Global ? "Global" : "Mainland China")})."
                : "Active is not logged in. Passive uses Kimi Code CLI credentials.";
        }
        catch (Exception) { if (!_closed) _kimiStatus.Text = "Unable to read Active credentials. Log in again."; }
    }

    private async Task LoginKimiAsync()
    {
        if (_kimiLoginCancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _kimiLoginCancellation = cancellation;
        UpdateKimiControls();
        _kimiStatus.Text = "Requesting Kimi login…";
        _kimiVerification.Text = string.Empty;
        var region = _kimiRegion.SelectedIndex == 1 ? KimiOAuthRegion.Global : KimiOAuthRegion.MainlandChina;
        try
        {
            await _kimiOAuth.LoginAsync(region, async data => await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_closed || cancellation.IsCancellationRequested) return;
                _kimiStatus.Text = "Complete authorization in your browser. Save settings to use Active mode.";
                _kimiVerification.Text = $"Code: {data.UserCode}\n{data.VerificationUri}";
                _kimiVerification.IsVisible = true;
                try { Process.Start(new ProcessStartInfo(data.VerificationUri.AbsoluteUri) { UseShellExecute = true }); }
                catch (Exception) { _kimiStatus.Text = "Open the link below in your browser and enter the code."; }
            }), cancellation.Token);
            if (!_closed) { _kimiVerification.Text = string.Empty; await RefreshKimiStatusAsync(); }
        }
        catch (OperationCanceledException) { if (!_closed) _kimiStatus.Text = "Kimi login cancelled or timed out."; }
        catch (Exception exception)
        {
            if (!_closed) _kimiStatus.Text = exception is UsageException
                ? exception.Message : "Unable to complete Kimi login. Try again.";
        }
        finally { _kimiLoginCancellation = null; if (!_closed) UpdateKimiControls(); }
    }

    private void HandleCancel(object? sender, RoutedEventArgs eventArgs) => Close(null);

    private void HandleSave(object? sender, RoutedEventArgs eventArgs) => Close(Settings);
}

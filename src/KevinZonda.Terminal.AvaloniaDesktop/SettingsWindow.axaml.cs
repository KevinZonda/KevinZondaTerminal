using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

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
    private readonly CheckBox _autoRenewKimi;
    private readonly ComboBox _shellExitBehavior;
    private bool _applyingValues;

    internal SettingsWindow(DesktopSettings settings)
    {
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
        _autoRenewKimi = Find<CheckBox>("AutoRenewKimiBox");
        _shellExitBehavior = Find<ComboBox>("ShellExitBehaviorBox");

        _fontFamily.ItemsSource = FontManager.Current.SystemFonts
            .Select(font => font.Name)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        _cursorShape.ItemsSource = new[] { "Block", "Underline", "Bar" };
        _theme.ItemsSource = DesktopTerminalThemeCatalog.All.Select(theme => theme.Name).ToArray();
        _shellExitBehavior.ItemsSource = new[] { "Keep tab open", "Close tab" };

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

    internal DesktopSettings Settings => DesktopSettings.Normalize(new DesktopSettings
    {
        Font = new DesktopFontSettings
        {
            Family = _fontFamily.Text ?? DesktopFontSettings.DefaultFamily,
            Size = decimal.ToDouble(_fontSize.Value ?? 14),
            LineHeight = decimal.ToDouble(_lineHeight.Value ?? 1.12m),
            EnableLigatures = _ligatures.IsChecked == true
        },
        Theme = new DesktopThemeSettings
        {
            Name = _theme.SelectedItem as string ?? DesktopTerminalThemeCatalog.DefaultName
        },
        Cursor = new DesktopCursorSettings
        {
            Shape = (_cursorShape.SelectedItem as string)?.ToLowerInvariant() ?? "bar",
            Blink = _cursorBlink.IsChecked == true
        },
        Indicators = new DesktopIndicatorSettings
        {
            ShowWorkspaceIndicator = _workspaceIndicator.IsChecked == true,
            ShowRemainingUsage = _remainingUsage.IsChecked == true,
            AutoRenewKimiToken = _autoRenewKimi.IsChecked == true
        },
        Shell = new DesktopShellSettings
        {
            ExitBehavior = _shellExitBehavior.SelectedIndex == 1 ? "CloseTab" : "KeepTab"
        }
    });

    private T Find<T>(string name) where T : Control =>
        this.FindControl<T>(name) ?? throw new InvalidOperationException(
            $"Settings control '{name}' was not created.");

    private void ApplyValues(DesktopSettings settings)
    {
        var normalized = DesktopSettings.Normalize(settings);
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
            _autoRenewKimi.IsChecked = normalized.Indicators.AutoRenewKimiToken;
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

        var preset = DesktopTerminalThemeCatalog.Find(_theme.SelectedItem as string);
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
        ApplyValues(new DesktopSettings());

    private void HandleCancel(object? sender, RoutedEventArgs eventArgs) => Close(null);

    private void HandleSave(object? sender, RoutedEventArgs eventArgs) => Close(Settings);
}

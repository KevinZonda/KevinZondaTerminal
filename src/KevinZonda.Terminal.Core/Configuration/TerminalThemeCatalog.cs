namespace KevinZonda.Terminal.Configuration;

internal sealed record TerminalThemePreset(
    string Name,
    string Background,
    string Foreground,
    string Cursor,
    string SelectionBackground,
    IReadOnlyList<string> AnsiColors);

internal static class TerminalThemeCatalog
{
    internal const string DefaultName = "KevinZonda Terminal Dark";

    internal static IReadOnlyList<TerminalThemePreset> All { get; } =
    [
        new(
            DefaultName,
            "#0c0f14",
            "#d8dee9",
            "#8fbcbb",
            "#3b5268",
            [
                "#1b2028", "#e06c75", "#98c379", "#e5c07b",
                "#61afef", "#c678dd", "#56b6c2", "#abb2bf",
                "#5c6370", "#e06c75", "#98c379", "#e5c07b",
                "#61afef", "#c678dd", "#56b6c2", "#ffffff"
            ]),
        new(
            "Pro",
            "#000000",
            "#f2f2f2",
            "#4d4d4d",
            "#414141",
            [
                "#000000", "#990000", "#00a600", "#999900",
                "#2009db", "#b200b2", "#00a6b2", "#bfbfbf",
                "#666666", "#e50000", "#00d900", "#e5e500",
                "#0000ff", "#e500e5", "#00e5e5", "#e5e5e5"
            ]),
        new(
            "Ubuntu",
            "#300a24",
            "#eeeeec",
            "#bbbbbb",
            "#b5d5ff",
            [
                "#2e3436", "#cc0000", "#4e9a06", "#c4a000",
                "#3465a4", "#75507b", "#06989a", "#d3d7cf",
                "#555753", "#ef2929", "#8ae234", "#fce94f",
                "#729fcf", "#ad7fa8", "#34e2e2", "#eeeeec"
            ]),
        new(
            "Campbell Powershell",
            "#012456",
            "#CCCCCC",
            "#FFFFFF",
            "#3b5268",
            [
                "#0C0C0C", "#C50F1F", "#13A10E", "#C19C00",
                "#0037DA", "#881798", "#3A96DD", "#CCCCCC",
                "#767676", "#E74856", "#16C60C", "#F9F1A5",
                "#3B78FF", "#B4009E", "#61D6D6", "#F2F2F2"
            ]),
        new(
            "Builtin Tango Dark",
            "#000000",
            "#ffffff",
            "#ffffff",
            "#b5d5ff",
            [
                "#000000", "#cc0000", "#4e9a06", "#c4a000",
                "#3465a4", "#75507b", "#06989a", "#d3d7cf",
                "#555753", "#ef2929", "#8ae234", "#fce94f",
                "#729fcf", "#ad7fa8", "#34e2e2", "#eeeeec"
            ]),
        new(
            "Campbell",
            "#0C0C0C",
            "#CCCCCC",
            "#FFFFFF",
            "#3b5268",
            [
                "#0C0C0C", "#C50F1F", "#13A10E", "#C19C00",
                "#0037DA", "#881798", "#3A96DD", "#CCCCCC",
                "#767676", "#E74856", "#16C60C", "#F9F1A5",
                "#3B78FF", "#B4009E", "#61D6D6", "#F2F2F2"
            ]),
        new(
            "IBM 5153",
            "#000000",
            "#AAAAAA",
            "#00AA00",
            "#FFFFFF",
            [
                "#000000", "#AA0000", "#00AA00", "#C47E00",
                "#0000AA", "#AA00AA", "#00AAAA", "#AAAAAA",
                "#555555", "#FF5555", "#55FF55", "#FFFF55",
                "#5555FF", "#FF55FF", "#55FFFF", "#FFFFFF"
            ])
    ];

    internal static TerminalThemePreset Find(string? name)
    {
        return All.FirstOrDefault(
            theme => string.Equals(theme.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? All[0];
    }
}

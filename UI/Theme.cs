namespace Asynkron.Swarm.UI;

public class Theme
{
    // Main text colors
    public string DefaultTextColor { get; init; } = "#abb2bf";
    public string DimTextColor { get; init; } = "#5c6370";

    // Message type colors (Say/Do/See)
    public string SayTextColor { get; init; } = "#abb2bf";
    public string DoTextColor { get; init; } = "#5c6370";
    public string SeeTextColor { get; init; } = "#4b5263";

    // UI element colors
    public string HeaderTextColor { get; init; } = "#61afef";
    public string AccentTextColor { get; init; } = "#e5c07b";
    public string FocusColor { get; init; } = "#61afef";
    public string BorderColor { get; init; } = "#5c6370";

    // Code and markdown colors
    public string CodeTextColor { get; init; } = "#d19a66";
    public string InlineCodeColor { get; init; } = "#e19df5";
    public string MarkdownHeaderColor { get; init; } = "#75c9fa";

    // Status colors
    public string SuccessColor { get; init; } = "#98c379";
    public string ErrorColor { get; init; } = "#e06c75";
    public string WarningColor { get; init; } = "#e5c07b";

    // Singleton for current theme
    public static Theme Current { get; set; } = OneDark;

    // Built-in themes
    public static Theme OneDark => new();

    public static Theme Dracula => new()
    {
        DefaultTextColor = "#f8f8f2",
        DimTextColor = "#6272a4",
        SayTextColor = "#f8f8f2",
        DoTextColor = "#6272a4",
        SeeTextColor = "#44475a",
        HeaderTextColor = "#bd93f9",
        AccentTextColor = "#ffb86c",
        FocusColor = "#bd93f9",
        BorderColor = "#6272a4",
        CodeTextColor = "#ffb86c",
        InlineCodeColor = "#ff79c6",
        MarkdownHeaderColor = "#8be9fd",
        SuccessColor = "#50fa7b",
        ErrorColor = "#ff5555",
        WarningColor = "#f1fa8c"
    };

    public static Theme Nord => new()
    {
        DefaultTextColor = "#d8dee9",
        DimTextColor = "#4c566a",
        SayTextColor = "#d8dee9",
        DoTextColor = "#4c566a",
        SeeTextColor = "#3b4252",
        HeaderTextColor = "#88c0d0",
        AccentTextColor = "#ebcb8b",
        FocusColor = "#88c0d0",
        BorderColor = "#4c566a",
        CodeTextColor = "#d08770",
        InlineCodeColor = "#b48ead",
        MarkdownHeaderColor = "#81a1c1",
        SuccessColor = "#a3be8c",
        ErrorColor = "#bf616a",
        WarningColor = "#ebcb8b"
    };

    public static Theme Monokai => new()
    {
        DefaultTextColor = "#f8f8f2",
        DimTextColor = "#75715e",
        SayTextColor = "#f8f8f2",
        DoTextColor = "#75715e",
        SeeTextColor = "#49483e",
        HeaderTextColor = "#66d9ef",
        AccentTextColor = "#e6db74",
        FocusColor = "#66d9ef",
        BorderColor = "#75715e",
        CodeTextColor = "#fd971f",
        InlineCodeColor = "#ae81ff",
        MarkdownHeaderColor = "#a6e22e",
        SuccessColor = "#a6e22e",
        ErrorColor = "#f92672",
        WarningColor = "#e6db74"
    };
}

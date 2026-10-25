using Pagewing.Core.Annotations;
using Pagewing.Core.Printing;
using Pagewing.Core.Viewing;

namespace Pagewing.Core.Settings;

/// <summary>Colour theme of the app.</summary>
public enum ThemeSetting
{
    /// <summary>Follow the operating system.</summary>
    System,

    /// <summary>Always light.</summary>
    Light,

    /// <summary>Always dark.</summary>
    Dark,
}

/// <summary>UI language of the app.</summary>
public enum LanguageSetting
{
    /// <summary>Follow the system language.</summary>
    System,

    /// <summary>German.</summary>
    German,

    /// <summary>English.</summary>
    English,
}

/// <summary>Spacing density of the UI.</summary>
public enum DensitySetting
{
    /// <summary>The standard sizes.</summary>
    Normal,

    /// <summary>Tighter sizes.</summary>
    Compact,
}

/// <summary>How the page itself is drawn when the theme is dark.</summary>
public enum PageAppearanceSetting
{
    /// <summary>The page stays as the PDF defines it.</summary>
    Light,

    /// <summary>The page follows the theme (dark pages in a dark theme).</summary>
    FollowTheme,
}

/// <summary>Which side panel opens by itself in a new window.</summary>
public enum StartPanelSetting
{
    /// <summary>No panel.</summary>
    None,

    /// <summary>The page thumbnails.</summary>
    Pages,

    /// <summary>The table of contents.</summary>
    Contents,
}

/// <summary>Whether the app offers to become the default app for PDF files.</summary>
public enum DefaultAppPromptSetting
{
    /// <summary>Ask at the first start of each run, while another app is the default.</summary>
    Ask,

    /// <summary>The user chose "Don't ask again".</summary>
    Never,
}

/// <summary>
/// The user's settings. Every property has a default; <see cref="Normalized"/> repairs values that came from a
/// hand-edited or older file. The properties have setters (not <c>init</c>) on purpose: for <c>init</c> properties the
/// JSON source generator hands <c>default</c> to every property missing in the file instead of keeping its initializer.
/// Treat instances as immutable and change them with <c>with</c>.
/// </summary>
public sealed record AppSettings
{
    /// <summary>The smallest panel width in DIP.</summary>
    public const double MinPanelWidth = 200;

    /// <summary>The largest panel width in DIP.</summary>
    public const double MaxPanelWidth = 400;

    /// <summary>The panel width in DIP used when none is stored.</summary>
    public const double DefaultPanelWidth = 264;

    /// <summary>The settings with every default value; every call returns a new instance, so callers may not share state.</summary>
    public static AppSettings Default => new();

    /// <summary>Version of the file format; a newer version than this app knows is tolerated.</summary>
    public int Version { get; set; } = 1;

    /// <summary>The colour theme.</summary>
    public ThemeSetting Theme { get; set; } = ThemeSetting.System;

    /// <summary>The UI language.</summary>
    public LanguageSetting Language { get; set; } = LanguageSetting.System;

    /// <summary>The spacing density.</summary>
    public DensitySetting Density { get; set; } = DensitySetting.Normal;

    /// <summary>Whether animations are reduced to a minimum.</summary>
    public bool ReduceMotion { get; set; }

    /// <summary>How pages look in a dark theme.</summary>
    public PageAppearanceSetting PageAppearance { get; set; } = PageAppearanceSetting.Light;

    /// <summary>The page layout a new tab starts with.</summary>
    public PageLayoutMode DefaultLayout { get; set; } = PageLayoutMode.Continuous;

    /// <summary>The zoom mode a new tab starts with; <see cref="ZoomMode.Free"/> is not a valid default.</summary>
    public ZoomMode DefaultZoomMode { get; set; } = ZoomMode.ActualSize;

    /// <summary>The panel that opens by itself in a new window.</summary>
    public StartPanelSetting StartPanel { get; set; } = StartPanelSetting.Pages;

    /// <summary>The width of the side panel in DIP, between <see cref="MinPanelWidth"/> and <see cref="MaxPanelWidth"/>.</summary>
    public double PanelWidth { get; set; } = DefaultPanelWidth;

    /// <summary>The longest name of a printer that is kept (longer names are dropped).</summary>
    public const int MaxPrinterNameLength = 256;

    /// <summary>The printer the user printed on last, or <see langword="null"/>.</summary>
    public string? LastPrinter { get; set; }

    /// <summary>How a page is fitted onto the paper when printing: to fit the paper, or at its actual size.</summary>
    public PrintScaling PrintScaling { get; set; } = PrintScaling.FitToPaper;

    /// <summary>Whether the card "Set Pagewing as default for PDFs?" may appear at the start.</summary>
    public DefaultAppPromptSetting DefaultAppPrompt { get; set; } = DefaultAppPromptSetting.Ask;

    /// <summary>Whether Pagewing checks for a newer version by itself (at most once in 24 hours). Off means no network access at all.</summary>
    public bool UpdateCheck { get; set; } = true;

    /// <summary>The longest author name that is kept (a longer name is dropped).</summary>
    public const int MaxAuthorNameLength = 128;

    /// <summary>
    /// The name written to the <c>/T</c> entry of every annotation the user creates, or <see langword="null"/> for none
    /// (the default: no name of the system user lands in a file that is shared).
    /// </summary>
    public string? AuthorName { get; set; }

    /// <summary>The colour the text highlight starts with (the toolbar remembers the last one chosen).</summary>
    public MarkupColor HighlightColor { get; set; } = MarkupColor.Yellow;

    /// <summary>The colour the underline starts with (the toolbar remembers the last one chosen).</summary>
    public MarkupColor UnderlineColor { get; set; } = MarkupColor.Blue;

    /// <summary>The colour the strike-out starts with (the toolbar remembers the last one chosen).</summary>
    public MarkupColor StrikeOutColor { get; set; } = MarkupColor.Pink;

    /// <summary>The kinds that have a tool style: the drawing tools (the text markups keep their colour in the three properties above).</summary>
    public static IReadOnlyList<AnnotationKind> ToolStyleKinds { get; } =
        [AnnotationKind.Ink, AnnotationKind.Rectangle, AnnotationKind.Ellipse, AnnotationKind.Line, AnnotationKind.Arrow, AnnotationKind.TextBox];

    /// <summary>The style a drawing tool starts with: red, medium.</summary>
    public static ToolStyle DefaultToolStyle { get; } = new(DrawColor.Red, StrokeWidth.Medium);

    /// <summary>
    /// The styles the user chose for the drawing tools (the toolbar remembers the last one chosen), or <see langword="null"/>
    /// while none was changed, so an untouched file stays small and two default settings are equal. A tool without an
    /// entry has <see cref="DefaultToolStyle"/>; read with <see cref="StyleOf"/>. After <see cref="Normalized"/> the keys are
    /// drawing tools only. A new dictionary is made for every change (<c>with</c>), the old one is never edited.
    /// </summary>
    public Dictionary<AnnotationKind, ToolStyle>? ToolStyles { get; set; }

    /// <summary>Gets the style of a tool: the chosen one, or <see cref="DefaultToolStyle"/> (also for a kind that is no drawing tool).</summary>
    /// <param name="kind">The kind of annotation the tool creates.</param>
    /// <returns>The style.</returns>
    public ToolStyle StyleOf(AnnotationKind kind) =>
        ToolStyles is not null && ToolStyleKinds.Contains(kind) && ToolStyles.TryGetValue(kind, out var style) && IsValid(style)
            ? style
            : DefaultToolStyle;

    /// <summary>
    /// Returns a copy in which <paramref name="kind"/> has <paramref name="style"/> and every other tool keeps its style.
    /// The copy has a new dictionary: the dictionary of this instance is never edited, so settings handed out earlier (and
    /// the equality of two settings, which compares the dictionary by reference) stay what they were.
    /// </summary>
    /// <param name="kind">The kind of annotation the tool creates; a kind that is no drawing tool changes nothing.</param>
    /// <param name="style">The style.</param>
    /// <returns>The changed copy.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="style"/> is <see langword="null"/>.</exception>
    public AppSettings WithToolStyle(AnnotationKind kind, ToolStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        if (!ToolStyleKinds.Contains(kind))
        {
            return this;
        }

        var styles = ToolStyles is null ? new Dictionary<AnnotationKind, ToolStyle>() : new Dictionary<AnnotationKind, ToolStyle>(ToolStyles);
        styles[kind] = style;
        return this with { ToolStyles = styles };
    }

    private static bool IsValid(ToolStyle? style) =>
        style is not null && Enum.IsDefined(style.Color) && Enum.IsDefined(style.Width);

    // Drops entries of unknown kinds and null values, and repairs an undefined colour or width to the default of that
    // part. Returns the same instance when nothing needs repair (so a normalized copy equals its source), null when
    // nothing is left.
    private static Dictionary<AnnotationKind, ToolStyle>? NormalizedToolStyles(Dictionary<AnnotationKind, ToolStyle>? styles)
    {
        if (styles is null)
        {
            return null;
        }

        if (styles.Count > 0 && styles.All(e => ToolStyleKinds.Contains(e.Key) && IsValid(e.Value)))
        {
            return styles;
        }

        var repaired = new Dictionary<AnnotationKind, ToolStyle>();
        foreach (var (kind, style) in styles)
        {
            if (style is not null && ToolStyleKinds.Contains(kind))
            {
                repaired[kind] = new ToolStyle(
                    Enum.IsDefined(style.Color) ? style.Color : DefaultToolStyle.Color,
                    Enum.IsDefined(style.Width) ? style.Width : DefaultToolStyle.Width);
            }
        }

        return repaired.Count > 0 ? repaired : null;
    }

    /// <summary>Returns a copy whose values are all valid: undefined enum values become the default, the width is clamped, an empty or overlong printer or author name is dropped.</summary>
    public AppSettings Normalized()
    {
        var defaults = Default;
        return this with
        {
            AuthorName = string.IsNullOrWhiteSpace(AuthorName) || AuthorName.Length > MaxAuthorNameLength ? null : AuthorName,
            HighlightColor = Enum.IsDefined(HighlightColor) ? HighlightColor : defaults.HighlightColor,
            UnderlineColor = Enum.IsDefined(UnderlineColor) ? UnderlineColor : defaults.UnderlineColor,
            StrikeOutColor = Enum.IsDefined(StrikeOutColor) ? StrikeOutColor : defaults.StrikeOutColor,
            ToolStyles = NormalizedToolStyles(ToolStyles),
            Theme = Enum.IsDefined(Theme) ? Theme : defaults.Theme,
            Language = Enum.IsDefined(Language) ? Language : defaults.Language,
            Density = Enum.IsDefined(Density) ? Density : defaults.Density,
            PageAppearance = Enum.IsDefined(PageAppearance) ? PageAppearance : defaults.PageAppearance,
            DefaultLayout = Enum.IsDefined(DefaultLayout) ? DefaultLayout : defaults.DefaultLayout,
            DefaultZoomMode = DefaultZoomMode is ZoomMode.ActualSize or ZoomMode.FitWidth or ZoomMode.FitPage
                ? DefaultZoomMode
                : defaults.DefaultZoomMode,
            StartPanel = Enum.IsDefined(StartPanel) ? StartPanel : defaults.StartPanel,
            PrintScaling = Enum.IsDefined(PrintScaling) ? PrintScaling : defaults.PrintScaling,
            DefaultAppPrompt = Enum.IsDefined(DefaultAppPrompt) ? DefaultAppPrompt : defaults.DefaultAppPrompt,
            LastPrinter = string.IsNullOrWhiteSpace(LastPrinter) || LastPrinter.Length > MaxPrinterNameLength ? null : LastPrinter,
            PanelWidth = double.IsFinite(PanelWidth) ? Math.Clamp(PanelWidth, MinPanelWidth, MaxPanelWidth) : DefaultPanelWidth,
        };
    }
}

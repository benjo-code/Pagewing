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

    /// <summary>Returns a copy whose values are all valid: undefined enum values become the default, the width is clamped.</summary>
    public AppSettings Normalized()
    {
        var defaults = Default;
        return this with
        {
            Theme = Enum.IsDefined(Theme) ? Theme : defaults.Theme,
            Language = Enum.IsDefined(Language) ? Language : defaults.Language,
            Density = Enum.IsDefined(Density) ? Density : defaults.Density,
            PageAppearance = Enum.IsDefined(PageAppearance) ? PageAppearance : defaults.PageAppearance,
            DefaultLayout = Enum.IsDefined(DefaultLayout) ? DefaultLayout : defaults.DefaultLayout,
            DefaultZoomMode = DefaultZoomMode is ZoomMode.ActualSize or ZoomMode.FitWidth or ZoomMode.FitPage
                ? DefaultZoomMode
                : defaults.DefaultZoomMode,
            StartPanel = Enum.IsDefined(StartPanel) ? StartPanel : defaults.StartPanel,
            PanelWidth = double.IsFinite(PanelWidth) ? Math.Clamp(PanelWidth, MinPanelWidth, MaxPanelWidth) : DefaultPanelWidth,
        };
    }
}

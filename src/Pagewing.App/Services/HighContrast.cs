using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Pagewing.App.Services;

/// <summary>
/// The fixed high-contrast palette of Pagewing (design system 5.1, column "Hochkontrast"): Windows asks for it through
/// <see cref="PlatformColorValues.ContrastPreference"/>; Linux reports no contrast request. <see cref="Appearance"/>
/// selects <see cref="Variant"/> when the theme setting is "System" and the system asks for high contrast.
/// </summary>
/// <remarks>
/// The tokens are an ordinary theme dictionary of the app (Styles/HighContrast.axaml under <see cref="Variant"/>), so
/// every <c>DynamicResource</c> follows a switch at runtime. Fluent cannot carry the palette itself:
/// <c>FluentTheme.Palettes</c> of Avalonia 12.1.4 throws for any variant other than Light and Dark, and its controls
/// resolve their brushes from the palette once (<c>StaticResource</c>), so only the accent of a palette follows a change.
/// Instead the tokens get a second Fluent theme of their own, whose Dark palette is the high-contrast one
/// (<see cref="CreateFluentPalette"/>); its brushes are looked up first while the variant is in effect. All of this is
/// created only on the first request, never in the startup path of a system without high contrast.
/// </remarks>
internal static class HighContrast
{
    /// <summary>
    /// The theme variant of high contrast. It inherits Dark, so a resource that is not a token (a size, a font) comes
    /// from the dark theme; the pages do not follow it (<see cref="ViewModels.MainViewModel.AppPageTheme"/> is dark only
    /// for <see cref="ThemeVariant.Dark"/>): they stay PDF content, black on white.
    /// </summary>
    public static readonly ThemeVariant Variant = new("HighContrast", ThemeVariant.Dark);

    /// <summary>
    /// Whether <paramref name="values"/> ask for high contrast (<see cref="ColorContrastPreference.High"/>); without
    /// colour values or without a contrast preference (Linux, headless) <see langword="false"/>.
    /// </summary>
    public static bool IsRequested(PlatformColorValues? values) => values?.ContrastPreference == ColorContrastPreference.High;

    /// <summary>
    /// Puts the high-contrast tokens and the Fluent resources of <see cref="Variant"/> into the theme dictionaries of
    /// <paramref name="app"/>, once; later calls find them there. UI thread only.
    /// </summary>
    public static void EnsureResources(Application app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var themes = app.Resources.ThemeDictionaries;
        if (themes.ContainsKey(Variant))
        {
            return;
        }

        var tokens = new Styles.HighContrast();

        // Merged dictionaries come after the dictionary's own entries: the tokens win, Fluent fills in the rest.
        tokens.MergedDictionaries.Add(new FluentResources(CreateFluentPalette(tokens)));
        themes[Variant] = tokens;
    }

    /// <summary>
    /// The Fluent palette of high contrast, built from the colour tokens of <paramref name="tokens"/> (the Fluent roles
    /// as in App.axaml for Light and Dark). <c>ChromeWhite</c> is what Fluent draws on the accent (check marks, switch
    /// knobs), so on the yellow accent it is accent.on (black).
    /// </summary>
    public static ColorPaletteResources CreateFluentPalette(IResourceDictionary tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        Color Token(string name) => tokens[$"Pw.Color.{name}"] is Color color
            ? color
            : throw new InvalidOperationException($"The high-contrast token {name} is missing.");

        var raised = Token("Surface.Raised");
        return new ColorPaletteResources
        {
            Accent = Token("Accent.Default"),
            RegionColor = Token("Surface.Window"),
            ErrorText = Token("Danger.Default"),
            BaseHigh = Token("Text.Primary"),
            BaseMediumHigh = Token("Text.Primary"),
            BaseMedium = Token("Text.Secondary"),
            BaseMediumLow = Token("Border.Strong"),
            BaseLow = Token("Surface.Hover"),
            AltHigh = raised,
            AltMediumHigh = Color.FromArgb(0xCC, raised.R, raised.G, raised.B),
            AltMedium = Color.FromArgb(0x99, raised.R, raised.G, raised.B),
            AltMediumLow = Color.FromArgb(0x66, raised.R, raised.G, raised.B),
            AltLow = Color.FromArgb(0x33, raised.R, raised.G, raised.B),
            ChromeHigh = Token("Border.Strong"),
            ChromeMedium = Token("Surface.Hover"),
            ChromeMediumLow = Token("Surface.Window"),
            ChromeLow = Token("Surface.Toolbar"),
            ChromeAltLow = Token("Text.Primary"),
            ChromeGray = Token("Text.Secondary"),
            ChromeWhite = Token("Accent.On"),
            ChromeBlackHigh = Color.FromArgb(0xFF, 0, 0, 0),
            ChromeBlackMedium = Color.FromArgb(0xCC, 0, 0, 0),
            ChromeBlackMediumLow = Color.FromArgb(0x66, 0, 0, 0),
            ChromeBlackLow = Color.FromArgb(0x33, 0, 0, 0),
            ChromeDisabledHigh = Token("Surface.Hover"),
            ChromeDisabledLow = Token("Text.Disabled"),
            ListMedium = Token("Accent.Subtle"),
            ListLow = Token("Surface.Hover"),
        };
    }

    /// <summary>
    /// The resources of a Fluent theme of its own whose Dark palette is the high-contrast one: its brushes resolve
    /// against that palette. Control themes are left to the Fluent theme of the app (no second set of templates).
    /// </summary>
    private sealed class FluentResources : ResourceProvider
    {
        private readonly FluentTheme _theme;

        // The owner of the theme: the brushes of Fluent that bind the accent with DynamicResource find it through this
        // scope, which is Dark and has no parent, so they get the accent of the high-contrast palette.
        private readonly ThemeVariantScope _host;

        public FluentResources(ColorPaletteResources palette)
        {
            _theme = new FluentTheme();
            _theme.Palettes[ThemeVariant.Dark] = palette;
            _host = new ThemeVariantScope { RequestedThemeVariant = ThemeVariant.Dark };
            _host.Styles.Add(_theme);
        }

        public override bool HasResources => true;

        public override bool TryGetResource(object key, ThemeVariant? theme, out object? value)
        {
            if (key is string && ((IResourceNode)_theme).TryGetResource(key, ThemeVariant.Dark, out value) && value is not ControlTheme)
            {
                return true;
            }

            value = null;
            return false;
        }
    }
}

/// <summary>The contrast request of the system (<see cref="PlatformContrastSource"/> in the app; tests give their own).</summary>
internal interface IContrastSource
{
    /// <summary>Raised when the colour values of the system change (theme, contrast, accent).</summary>
    event Action? ColorValuesChanged;

    /// <summary>The colour values of the system now, or <see langword="null"/> without platform settings.</summary>
    PlatformColorValues? GetColorValues();
}

/// <summary>
/// The contrast request from the platform settings of the app (<see cref="Application.PlatformSettings"/>): Windows
/// reports it and announces changes (<see cref="IPlatformSettings.ColorValuesChanged"/>); Linux and the headless
/// platform report no preference. Reading is cheap: Avalonia keeps the values it read for the theme of the app.
/// </summary>
internal sealed class PlatformContrastSource : IContrastSource, IDisposable
{
    private readonly IPlatformSettings? _settings;

    public PlatformContrastSource(IPlatformSettings? settings)
    {
        _settings = settings;
        if (settings is not null)
        {
            settings.ColorValuesChanged += OnColorValuesChanged;
        }
    }

    public event Action? ColorValuesChanged;

    public PlatformColorValues? GetColorValues() => _settings?.GetColorValues();

    public void Dispose()
    {
        if (_settings is not null)
        {
            _settings.ColorValuesChanged -= OnColorValuesChanged;
        }
    }

    private void OnColorValuesChanged(object? sender, PlatformColorValues e) => ColorValuesChanged?.Invoke();
}

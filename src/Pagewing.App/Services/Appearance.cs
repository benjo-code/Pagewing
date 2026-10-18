using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using Pagewing.Core.Settings;

namespace Pagewing.App.Services;

/// <summary>
/// Puts the appearance settings into effect on the app, at the start and whenever they change (no restart): the theme
/// (<see cref="ApplyTheme"/>), the motion (<see cref="ApplyMotion"/>) and the density (<see cref="ApplyDensity"/>).
/// Follows the theme of the system while the setting is <see cref="ThemeSetting.System"/> (Avalonia resolves
/// <see cref="ThemeVariant.Default"/> from the platform); when the system asks for high contrast (Windows), "System"
/// selects the fixed palette of <see cref="HighContrast.Variant"/> instead, and follows the request at runtime. A theme
/// chosen by the user (light or dark) beats the request. Announces the theme that is in effect
/// (<see cref="EffectiveThemeChanged"/>). UI thread only.
/// </summary>
public sealed class Appearance : IDisposable, IEffectiveTheme
{
    /// <summary>The resource with the duration of opening a panel or popup (design system 5.6), a <see cref="TimeSpan"/>.</summary>
    public const string OpenDurationKey = "Pw.Duration.Open";

    /// <summary>The resource with the duration of closing a panel or popup (design system 5.6), a <see cref="TimeSpan"/>.</summary>
    public const string CloseDurationKey = "Pw.Duration.Close";

    private readonly Application _app;
    private readonly UserSettings _settings;
    private readonly IContrastSource _contrast;
    private readonly bool _ownsContrast;
    private ThemeVariant _effectiveTheme;

    // Whether the system asks for high contrast, as last read from _contrast.
    private bool _highContrastRequested;

    // Whether the app's resources hold the zero durations of reduced motion, and the durations they replaced (the
    // compiled App.axaml merges Metrics.axaml into the app's own dictionary, so the zero values overwrite them).
    private bool _motionReduced;
    private object? _fullOpenDuration;
    private object? _fullCloseDuration;

    // While the density is "Kompakt": the normal values the compact ones replaced in the app's resources, per key (null:
    // the key was not in the app's own dictionary).
    private Dictionary<object, object?>? _normalSizes;

    /// <summary>
    /// Applies the current settings to <paramref name="app"/> and follows their changes and the contrast request of the
    /// platform (<see cref="Application.PlatformSettings"/>).
    /// </summary>
    public Appearance(Application app, UserSettings settings)
        : this(app, settings, new PlatformContrastSource(app?.PlatformSettings), ownsContrast: true)
    {
    }

    /// <summary>Applies the current settings to <paramref name="app"/> with the contrast request of <paramref name="contrast"/>.</summary>
    internal Appearance(Application app, UserSettings settings, IContrastSource contrast)
        : this(app, settings, contrast, ownsContrast: false)
    {
    }

    private Appearance(Application? app, UserSettings settings, IContrastSource contrast, bool ownsContrast)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(contrast);
        _app = app;
        _settings = settings;
        _contrast = contrast;
        _ownsContrast = ownsContrast;

        // Read once at the start (Avalonia keeps the values it read for the theme); without a request nothing of high
        // contrast is loaded.
        _highContrastRequested = HighContrast.IsRequested(contrast.GetColorValues());
        Apply(settings.Current);
        _effectiveTheme = app.ActualThemeVariant;
        settings.Changed += Apply;
        app.ActualThemeVariantChanged += OnActualThemeVariantChanged;
        contrast.ColorValuesChanged += OnColorValuesChanged;
    }

    /// <summary>
    /// Raised when the theme in effect changes, by a choice of the user or, with <see cref="ThemeSetting.System"/>, by
    /// the system.
    /// </summary>
    public event Action? EffectiveThemeChanged;

    /// <summary>
    /// Gets the theme in effect: the chosen one, or the one of the system (light or dark, or
    /// <see cref="HighContrast.Variant"/> when the system asks for high contrast).
    /// </summary>
    public ThemeVariant EffectiveTheme => _app.ActualThemeVariant;

    /// <summary>
    /// The duration of <paramref name="key"/> (<see cref="OpenDurationKey"/> or <see cref="CloseDurationKey"/>) as
    /// <paramref name="element"/> finds it now; zero with reduced motion. Read when an animation starts, so a change of
    /// the setting applies to the next one. <paramref name="fallback"/> when the resource is missing (no app).
    /// </summary>
    public static TimeSpan Duration(IResourceHost element, string key, TimeSpan fallback)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.TryFindResource(key, (element as IThemeVariantHost)?.ActualThemeVariant, out var value) && value is TimeSpan duration
            ? duration
            : fallback;
    }

    /// <summary>Stops following the settings and the theme; what was applied stays.</summary>
    public void Dispose()
    {
        _settings.Changed -= Apply;
        _app.ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        _contrast.ColorValuesChanged -= OnColorValuesChanged;
        if (_ownsContrast && _contrast is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private void Apply(AppSettings settings)
    {
        ApplyTheme(settings);
        ApplyMotion(settings);
        ApplyDensity(settings);
    }

    private void ApplyTheme(AppSettings settings)
    {
        var requested = settings.Theme switch
        {
            ThemeSetting.Light => ThemeVariant.Light,
            ThemeSetting.Dark => ThemeVariant.Dark,
            _ when _highContrastRequested => HighContrast.Variant,
            _ => ThemeVariant.Default,
        };

        if (Equals(requested, HighContrast.Variant))
        {
            // Loaded with the first request, before the variant asks for its resources.
            HighContrast.EnsureResources(_app);
        }

        // App.axaml asks for Default already: the start with the default setting changes nothing.
        if (!Equals(_app.RequestedThemeVariant, requested))
        {
            _app.RequestedThemeVariant = requested;
        }
    }

    // Reduced motion puts zero durations into the app's resources; full motion puts the durations of Metrics.axaml
    // back. With the default setting nothing is touched.
    private void ApplyMotion(AppSettings settings)
    {
        if (settings.ReduceMotion == _motionReduced)
        {
            return;
        }

        _motionReduced = settings.ReduceMotion;
        var resources = _app.Resources;
        if (_motionReduced)
        {
            _fullOpenDuration = resources.TryGetValue(OpenDurationKey, out var open) ? open : null;
            _fullCloseDuration = resources.TryGetValue(CloseDurationKey, out var close) ? close : null;
            resources[OpenDurationKey] = TimeSpan.Zero;
            resources[CloseDurationKey] = TimeSpan.Zero;
        }
        else
        {
            Restore(resources, OpenDurationKey, _fullOpenDuration);
            Restore(resources, CloseDurationKey, _fullCloseDuration);
        }
    }

    // "Kompakt" copies the values of Styles/Density.axaml over the normal ones of Metrics.axaml in the app's resources
    // (DynamicResource follows at once, no restart); "Normal" puts the normal values back. With the default setting nothing
    // is created or touched, so the start costs nothing.
    private void ApplyDensity(AppSettings settings)
    {
        var compact = settings.Density == DensitySetting.Compact;
        if (compact == (_normalSizes is not null))
        {
            return;
        }

        var resources = _app.Resources;
        if (compact)
        {
            var normal = new Dictionary<object, object?>();
            foreach (var (key, value) in new Styles.Density())
            {
                normal[key] = resources.TryGetValue(key, out var current) ? current : null;
                resources[key] = value;
            }

            _normalSizes = normal;
        }
        else
        {
            foreach (var (key, value) in _normalSizes!)
            {
                Restore(resources, key, value);
            }

            _normalSizes = null;
        }
    }

    private static void Restore(IResourceDictionary resources, object key, object? value)
    {
        if (value is null)
        {
            // The value lived in a merged dictionary that the replacement only hid.
            resources.Remove(key);
        }
        else
        {
            resources[key] = value;
        }
    }

    private void OnColorValuesChanged()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(OnColorValuesChanged);
            return;
        }

        UpdateContrastRequest();
    }

    // Reads the contrast request again and applies the theme when it changed; true when that changed the variant.
    private bool UpdateContrastRequest()
    {
        var requested = HighContrast.IsRequested(_contrast.GetColorValues());
        if (requested == _highContrastRequested)
        {
            return false;
        }

        _highContrastRequested = requested;
        var before = _app.RequestedThemeVariant;
        ApplyTheme(_settings.Current);
        return !Equals(before, _app.RequestedThemeVariant);
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        // The app follows the colour values of the platform before this class hears of them (it subscribed first): a
        // switch to high contrast would show the dark theme for a moment, and the pages that follow the theme would
        // turn dark and back. Reading the request here selects the right variant first; its own change announces it.
        if (UpdateContrastRequest())
        {
            return;
        }

        var effective = _app.ActualThemeVariant;
        if (Equals(effective, _effectiveTheme))
        {
            return;
        }

        _effectiveTheme = effective;
        EffectiveThemeChanged?.Invoke();
    }
}

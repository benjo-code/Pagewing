using System.Globalization;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.Core.Settings;
using Pagewing.Core.Viewing;

namespace Pagewing.App.ViewModels;

/// <summary>What the settings card shows of a value a setting can take (its lists show every value with this).</summary>
public interface ISettingsChoice
{
    /// <summary>Gets the text of the value, also its accessible name.</summary>
    string Title { get; }
}

/// <summary>A value a setting can take, with the text the settings card shows for it (also its accessible name).</summary>
/// <typeparam name="T">The type of the setting.</typeparam>
/// <param name="Value">The value.</param>
/// <param name="Title">What the card shows.</param>
public sealed record SettingsChoice<T>(T Value, string Title) : ISettingsChoice
{
    /// <summary>The title: a combo box shows its items with it.</summary>
    public override string ToString() => Title;
}

/// <summary>
/// The settings card (Ctrl+Comma, the settings button of the action bar): every option of the user's settings that the
/// card offers, two-way. Every change goes into the settings at once (<see cref="UserSettings.Update"/>: announced, put
/// into effect and written); there is no "Apply". Made by the window's view model when the card is first shown
/// (<see cref="MainViewModel.SettingsCard"/>), never in the startup path; while it is shown it follows changes of the
/// settings made elsewhere (another window). UI thread only.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    /// <summary>The name of the German language in the list of languages; the names of languages are not translated.</summary>
    internal const string GermanName = "Deutsch";

    /// <summary>The name of the English language in the list of languages.</summary>
    internal const string EnglishName = "English";

    private readonly UserSettings _settings;
    private readonly LanguageSetting _runningLanguage;
    private readonly LanguageSetting _systemLanguage;
    private bool _saveFailed;
    private bool _isOpen;

    /// <summary>
    /// Creates the card's view model for <paramref name="settings"/>. The language that runs is the one of the interface
    /// (<see cref="Strings.Culture"/>, chosen at the start and not changed later), the language of the system the one of
    /// <see cref="CultureInfo.CurrentUICulture"/>.
    /// </summary>
    public SettingsViewModel(UserSettings settings)
        : this(settings, Strings.Culture, CultureInfo.CurrentUICulture)
    {
    }

    /// <summary>Creates the view model with the language that runs and the one of the system given (tests).</summary>
    internal SettingsViewModel(UserSettings settings, CultureInfo running, CultureInfo system)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(running);
        ArgumentNullException.ThrowIfNull(system);
        _settings = settings;
        _runningLanguage = LanguageOf(running);
        _systemLanguage = LanguageOf(system);
        _saveFailed = settings.LastSaveFailed;

        ThemeChoices =
        [
            new(ThemeSetting.System, Strings.SettingsThemeSystem),
            new(ThemeSetting.Light, Strings.SettingsThemeLight),
            new(ThemeSetting.Dark, Strings.SettingsThemeDark),
        ];
        DensityChoices =
        [
            new(DensitySetting.Normal, Strings.SettingsDensityNormal),
            new(DensitySetting.Compact, Strings.SettingsDensityCompact),
        ];
        LayoutChoices =
        [
            new(PageLayoutMode.SinglePage, Strings.LayoutSingle),
            new(PageLayoutMode.Continuous, Strings.LayoutContinuous),
            new(PageLayoutMode.TwoPages, Strings.LayoutTwoPages),
            new(PageLayoutMode.TwoPagesWithCover, Strings.LayoutTwoPagesCover),
        ];

        // The modes of the zoom menu in its order; the free zoom is no default (AppSettings.DefaultZoomMode).
        ZoomModeChoices =
        [
            new(ZoomMode.FitWidth, Strings.ZoomFitWidth),
            new(ZoomMode.FitPage, Strings.ZoomFitPage),
            new(ZoomMode.ActualSize, Strings.ZoomActualSize),
        ];
        LanguageChoices =
        [
            new(LanguageSetting.System, Strings.SettingsLanguageSystem),
            new(LanguageSetting.German, GermanName),
            new(LanguageSetting.English, EnglishName),
        ];
    }

    /// <summary>Gets the colour themes the card offers.</summary>
    public IReadOnlyList<SettingsChoice<ThemeSetting>> ThemeChoices { get; }

    /// <summary>Gets the densities the card offers.</summary>
    public IReadOnlyList<SettingsChoice<DensitySetting>> DensityChoices { get; }

    /// <summary>Gets the page layouts the card offers as the default of new tabs.</summary>
    public IReadOnlyList<SettingsChoice<PageLayoutMode>> LayoutChoices { get; }

    /// <summary>Gets the zoom modes the card offers as the default of new tabs (without the free zoom).</summary>
    public IReadOnlyList<SettingsChoice<ZoomMode>> ZoomModeChoices { get; }

    /// <summary>Gets the languages the card offers.</summary>
    public IReadOnlyList<SettingsChoice<LanguageSetting>> LanguageChoices { get; }

    /// <summary>Gets or sets the colour theme; it applies at once.</summary>
    public ThemeSetting Theme
    {
        get => _settings.Current.Theme;
        set => Change(current => current with { Theme = value });
    }

    /// <summary>Gets or sets the language of the interface; it applies from the next start (<see cref="LanguageNeedsRestart"/>).</summary>
    public LanguageSetting Language
    {
        get => _settings.Current.Language;
        set => Change(current => current with { Language = value });
    }

    /// <summary>Gets or sets the density; it applies at once.</summary>
    public DensitySetting Density
    {
        get => _settings.Current.Density;
        set => Change(current => current with { Density = value });
    }

    /// <summary>Gets or sets whether animations are reduced (every duration 0); it applies to the next animation.</summary>
    public bool ReduceMotion
    {
        get => _settings.Current.ReduceMotion;
        set => Change(current => current with { ReduceMotion = value });
    }

    /// <summary>
    /// Gets or sets how the pages look in a dark theme; it applies at once to every tab that has no page theme of its own
    /// (<see cref="TabViewModel.PageThemeOverride"/>).
    /// </summary>
    public PageAppearanceSetting PageAppearance
    {
        get => _settings.Current.PageAppearance;
        set => Change(current => current with { PageAppearance = value });
    }

    /// <summary>Gets or sets <see cref="PageAppearance"/> as the switch of the card shows it: on for <see cref="PageAppearanceSetting.FollowTheme"/>.</summary>
    public bool DarkPages
    {
        get => PageAppearance == PageAppearanceSetting.FollowTheme;
        set => PageAppearance = value ? PageAppearanceSetting.FollowTheme : PageAppearanceSetting.Light;
    }

    /// <summary>Gets or sets the page layout new tabs start with; the tabs that are open keep theirs.</summary>
    public PageLayoutMode DefaultLayout
    {
        get => _settings.Current.DefaultLayout;
        set => Change(current => current with { DefaultLayout = value });
    }

    /// <summary>
    /// Gets or sets the zoom mode new tabs start with (<see cref="ZoomMode.FitWidth"/>, <see cref="ZoomMode.FitPage"/> or
    /// <see cref="ZoomMode.ActualSize"/>; the free zoom is ignored); the tabs that are open keep theirs.
    /// </summary>
    public ZoomMode DefaultZoomMode
    {
        get => _settings.Current.DefaultZoomMode;
        set
        {
            if (value is ZoomMode.FitWidth or ZoomMode.FitPage or ZoomMode.ActualSize)
            {
                Change(current => current with { DefaultZoomMode = value });
            }
        }
    }

    /// <summary>Gets or sets the chosen entry of <see cref="ThemeChoices"/> (the card's list).</summary>
    public SettingsChoice<ThemeSetting>? SelectedTheme
    {
        get => Find(ThemeChoices, Theme);
        set => Choose(value, choice => Theme = choice);
    }

    /// <summary>Gets or sets the chosen entry of <see cref="DensityChoices"/>.</summary>
    public SettingsChoice<DensitySetting>? SelectedDensity
    {
        get => Find(DensityChoices, Density);
        set => Choose(value, choice => Density = choice);
    }

    /// <summary>Gets or sets the chosen entry of <see cref="LayoutChoices"/>.</summary>
    public SettingsChoice<PageLayoutMode>? SelectedLayout
    {
        get => Find(LayoutChoices, DefaultLayout);
        set => Choose(value, choice => DefaultLayout = choice);
    }

    /// <summary>Gets or sets the chosen entry of <see cref="ZoomModeChoices"/>.</summary>
    public SettingsChoice<ZoomMode>? SelectedZoomMode
    {
        get => Find(ZoomModeChoices, DefaultZoomMode);
        set => Choose(value, choice => DefaultZoomMode = choice);
    }

    /// <summary>Gets or sets the chosen entry of <see cref="LanguageChoices"/>.</summary>
    public SettingsChoice<LanguageSetting>? SelectedLanguage
    {
        get => Find(LanguageChoices, Language);
        set => Choose(value, choice => Language = choice);
    }

    /// <summary>
    /// Gets whether the chosen language differs from the one the interface runs in ("System" stands for the language of
    /// the system): the card says that it applies from the next start.
    /// </summary>
    public bool LanguageNeedsRestart => (Language == LanguageSetting.System ? _systemLanguage : Language) != _runningLanguage;

    /// <summary>
    /// Gets whether the settings could not be written the last time the card changed them (or before it was shown): the
    /// card says so with a symbol and a text. The values stay in effect; the next successful write clears it.
    /// </summary>
    public bool SaveFailed
    {
        get => _saveFailed;
        private set => SetProperty(ref _saveFailed, value);
    }

    /// <summary>The window shows the card: it reads the settings again and follows their changes until <see cref="OnClosed"/>.</summary>
    internal void OnOpened()
    {
        if (_isOpen)
        {
            return;
        }

        _isOpen = true;
        _settings.Changed += OnSettingsChanged;
        SaveFailed = _settings.LastSaveFailed;
        RaiseAll();
    }

    /// <summary>The window no longer shows the card: it stops following the settings (they outlive the window).</summary>
    internal void OnClosed()
    {
        if (!_isOpen)
        {
            return;
        }

        _isOpen = false;
        _settings.Changed -= OnSettingsChanged;
    }

    /// <summary>German for the two-letter language <c>de</c>, English for everything else (as <see cref="Strings"/> chooses).</summary>
    private static LanguageSetting LanguageOf(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName == "de" ? LanguageSetting.German : LanguageSetting.English;

    private static SettingsChoice<T>? Find<T>(IReadOnlyList<SettingsChoice<T>> choices, T value) =>
        choices.FirstOrDefault(choice => EqualityComparer<T>.Default.Equals(choice.Value, value));

    // A list without a chosen entry (null, while its items change) changes nothing.
    private static void Choose<T>(SettingsChoice<T>? choice, Action<T> set)
    {
        if (choice is not null)
        {
            set(choice.Value);
        }
    }

    private void Change(Func<AppSettings, AppSettings> change)
    {
        var before = _settings.Current;
        _settings.Update(change);
        SaveFailed = _settings.LastSaveFailed;

        // While shown, the event of the change raised everything already; otherwise (a test, or the card closed) here.
        if (!_isOpen && !ReferenceEquals(before, _settings.Current))
        {
            RaiseAll();
        }
    }

    private void OnSettingsChanged(AppSettings settings) => RaiseAll();

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(Theme));
        OnPropertyChanged(nameof(Language));
        OnPropertyChanged(nameof(Density));
        OnPropertyChanged(nameof(ReduceMotion));
        OnPropertyChanged(nameof(PageAppearance));
        OnPropertyChanged(nameof(DarkPages));
        OnPropertyChanged(nameof(DefaultLayout));
        OnPropertyChanged(nameof(DefaultZoomMode));
        OnPropertyChanged(nameof(SelectedTheme));
        OnPropertyChanged(nameof(SelectedDensity));
        OnPropertyChanged(nameof(SelectedLayout));
        OnPropertyChanged(nameof(SelectedZoomMode));
        OnPropertyChanged(nameof(SelectedLanguage));
        OnPropertyChanged(nameof(LanguageNeedsRestart));
    }
}

using Avalonia.Styling;

namespace Pagewing.App.Services;

/// <summary>
/// The theme in effect, as the window view models need it (<see cref="Appearance"/> in the app; tests give their own):
/// with <see cref="Pagewing.Core.Settings.PageAppearanceSetting.FollowTheme"/> a dark theme makes the pages dark. UI
/// thread only.
/// </summary>
internal interface IEffectiveTheme
{
    /// <summary>Raised when <see cref="EffectiveTheme"/> changes.</summary>
    event Action? EffectiveThemeChanged;

    /// <summary>
    /// Gets the theme in effect: light or dark, or <see cref="HighContrast.Variant"/>, which inherits Dark but leaves
    /// the pages light.
    /// </summary>
    ThemeVariant EffectiveTheme { get; }
}

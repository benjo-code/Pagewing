using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Pagewing.App.Styles;

/// <summary>
/// The high-contrast tokens (HighContrast.axaml), compiled; created only when the system asks for high contrast
/// (<see cref="Services.HighContrast"/>).
/// </summary>
public sealed partial class HighContrast : ResourceDictionary
{
    public HighContrast() => AvaloniaXamlLoader.Load(this);
}

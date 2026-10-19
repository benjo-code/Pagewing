using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Pagewing.App.Styles;

/// <summary>The resources of the density "Kompakt" (Density.axaml), compiled; created only when that density applies.</summary>
public sealed partial class Density : ResourceDictionary
{
    public Density() => AvaloniaXamlLoader.Load(this);
}

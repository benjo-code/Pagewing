using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;

namespace Pagewing.App.Controls;

/// <summary>
/// The face of a blank page in the page thumbnails (mode "Organize pages"): drawn, never rendered. A page is white paper
/// whatever the theme (a rendered page is, too); a dark page (<see cref="IsDark"/>) has the ground of a dark page
/// (<c>Pw.Brush.Page.DarkBackground</c>). Decorative: the entry carries the words "Blank page".
/// </summary>
internal sealed class BlankPageFace : Control
{
    /// <summary>Defines the <see cref="IsDark"/> property.</summary>
    public static readonly StyledProperty<bool> IsDarkProperty =
        AvaloniaProperty.Register<BlankPageFace, bool>(nameof(IsDark));

    static BlankPageFace()
    {
        IsHitTestVisibleProperty.OverrideDefaultValue<BlankPageFace>(false);
        FocusableProperty.OverrideDefaultValue<BlankPageFace>(false);
        AutomationProperties.AccessibilityViewProperty.OverrideDefaultValue<BlankPageFace>(AccessibilityView.Raw);
        AffectsRender<BlankPageFace>(IsDarkProperty);
    }

    /// <summary>Gets or sets whether the page is drawn dark (the theme of the pages is dark).</summary>
    public bool IsDark
    {
        get => GetValue(IsDarkProperty);
        set => SetValue(IsDarkProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        IBrush fill = Brushes.White;
        if (IsDark && this.TryFindResource("Pw.Brush.Page.DarkBackground", ActualThemeVariant, out var dark) && dark is IBrush brush)
        {
            fill = brush;
        }

        context.FillRectangle(fill, new Rect(Bounds.Size));
    }
}

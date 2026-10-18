using Avalonia;
using Avalonia.Controls;
using Pagewing.Core.Viewing;

namespace Pagewing.App.Controls;

/// <summary>
/// Lays out the tabs of the tab strip side by side, one child per tab in the order of the strip, all as wide as
/// <see cref="TabStripLayout.Compute"/> says for the available width, in whole pixels (design system, section 6).
/// When even tabs of
/// <see cref="TabStripLayout.MinTabWidth"/> do not fit, only <see cref="ActiveItem"/> stays: the other children get
/// no size and become invisible (<see cref="ShowOnlyActive"/>). The panel owns the visibility of its children.
/// While a tab is dragged (<see cref="DragItem"/>), that child is shifted by <see cref="DragOffset"/> from its slot.
/// </summary>
public sealed class TabStripPanel : Panel
{
    public static readonly StyledProperty<double> ReservedWidthProperty =
        AvaloniaProperty.Register<TabStripPanel, double>(nameof(ReservedWidth));

    public static readonly StyledProperty<Control?> ActiveItemProperty =
        AvaloniaProperty.Register<TabStripPanel, Control?>(nameof(ActiveItem));

    public static readonly StyledProperty<Control?> DragItemProperty =
        AvaloniaProperty.Register<TabStripPanel, Control?>(nameof(DragItem));

    public static readonly StyledProperty<double> DragOffsetProperty =
        AvaloniaProperty.Register<TabStripPanel, double>(nameof(DragOffset));

    public static readonly DirectProperty<TabStripPanel, bool> ShowOnlyActiveProperty =
        AvaloniaProperty.RegisterDirect<TabStripPanel, bool>(nameof(ShowOnlyActive), panel => panel.ShowOnlyActive);

    private bool _showOnlyActive;

    static TabStripPanel()
    {
        AffectsMeasure<TabStripPanel>(ReservedWidthProperty, ActiveItemProperty);
        AffectsArrange<TabStripPanel>(DragItemProperty, DragOffsetProperty);
    }

    /// <summary>
    /// Gets or sets the width the panel leaves for what follows the tabs besides the drag area (the "Open file"
    /// button): the tabs share the available width minus this with the drag area.
    /// </summary>
    public double ReservedWidth
    {
        get => GetValue(ReservedWidthProperty);
        set => SetValue(ReservedWidthProperty, value);
    }

    /// <summary>Gets or sets the child of the active tab; the only one shown when not all tabs fit.</summary>
    public Control? ActiveItem
    {
        get => GetValue(ActiveItemProperty);
        set => SetValue(ActiveItemProperty, value);
    }

    /// <summary>Gets or sets the child that is being dragged, or <see langword="null"/>.</summary>
    public Control? DragItem
    {
        get => GetValue(DragItemProperty);
        set => SetValue(DragItemProperty, value);
    }

    /// <summary>Gets or sets how far <see cref="DragItem"/> is drawn right of its slot (negative: left).</summary>
    public double DragOffset
    {
        get => GetValue(DragOffsetProperty);
        set => SetValue(DragOffsetProperty, value);
    }

    /// <summary>Gets whether only <see cref="ActiveItem"/> is shown, as of the last measure.</summary>
    public bool ShowOnlyActive
    {
        get => _showOnlyActive;
        private set => SetAndRaise(ShowOnlyActiveProperty, ref _showOnlyActive, value);
    }

    /// <summary>Gets the width of each shown tab, as of the last measure.</summary>
    public double TabWidth { get; private set; } = TabStripLayout.MaxTabWidth;

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = Children.Count;
        var (computed, onlyActive) = TabStripLayout.Compute(count, availableSize.Width - ReservedWidth);

        // Whole pixels, so the tabs keep their width and edges under layout rounding; the rest goes to the drag area.
        var tabWidth = Math.Floor(computed);
        TabWidth = tabWidth;
        ShowOnlyActive = onlyActive;

        var height = 0.0;
        foreach (var child in Children)
        {
            var shown = !onlyActive || ReferenceEquals(child, ActiveItem);
            if (child.IsVisible != shown)
            {
                child.SetCurrentValue(IsVisibleProperty, shown);
            }

            child.Measure(new Size(tabWidth, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        var shownCount = onlyActive ? Math.Min(1, count) : count;
        return new Size(shownCount * tabWidth, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var slot = 0;
        foreach (var child in Children)
        {
            if (!child.IsVisible)
            {
                child.Arrange(default);
                continue;
            }

            var x = slot * TabWidth;
            if (ReferenceEquals(child, DragItem))
            {
                x += DragOffset;
            }

            child.Arrange(new Rect(x, 0, TabWidth, finalSize.Height));
            slot++;
        }

        return finalSize;
    }
}

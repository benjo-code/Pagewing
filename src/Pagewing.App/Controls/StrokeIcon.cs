using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Pagewing.App.Controls;

/// <summary>
/// Draws a line icon (Tabler "outline" style) from a geometry on a 24 x 24 grid with a 2 unit round stroke.
/// <c>PathIcon</c> only fills, so outline icons need this control. The geometry is scaled uniformly from the
/// 24 x 24 grid to the smaller side of the control, so the stroke keeps its 2/24 proportion at every size.
/// The icon is decorative: it is not hit-testable, not focusable and hidden from accessibility; the parent control
/// carries the name.
/// </summary>
public sealed class StrokeIcon : Control
{
    /// <summary>Edge length of the icon grid the geometries are drawn on.</summary>
    public const double Grid = 24;

    /// <summary>Stroke thickness in grid units.</summary>
    public const double StrokeThicknessInGrid = 2;

    /// <summary>Size of an icon without an explicit <c>Width</c>/<c>Height</c> (panel, tab and status bar size).</summary>
    public const double DefaultSize = 16;

    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<StrokeIcon, Geometry?>(nameof(Data));

    /// <summary>Stroke brush. When <c>null</c>, the icon uses the inherited <see cref="TextElement.ForegroundProperty"/>, so it follows the state colours of its button.</summary>
    public static readonly StyledProperty<IBrush?> BrushProperty =
        AvaloniaProperty.Register<StrokeIcon, IBrush?>(nameof(Brush));

    static StrokeIcon()
    {
        // The size is fixed (default or explicit), so neither property changes the layout.
        AffectsRender<StrokeIcon>(DataProperty, BrushProperty, TextElement.ForegroundProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<StrokeIcon>(false);
        AutomationProperties.AccessibilityViewProperty.OverrideDefaultValue<StrokeIcon>(AccessibilityView.Raw);
    }

    /// <summary>Geometry on the 24 x 24 grid.</summary>
    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Brush
    {
        get => GetValue(BrushProperty);
        set => SetValue(BrushProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(DefaultSize, DefaultSize);

    public override void Render(DrawingContext context)
    {
        var geometry = Data;
        var brush = Brush ?? TextElement.GetForeground(this);
        var edge = Math.Min(Bounds.Width, Bounds.Height);
        if (geometry is null || brush is null || edge <= 0)
        {
            return;
        }

        var scale = edge / Grid;
        var offset = new Vector((Bounds.Width - edge) / 2, (Bounds.Height - edge) / 2);
        var pen = new Pen(brush, StrokeThicknessInGrid, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y)))
        {
            context.DrawGeometry(null, pen, geometry);
        }
    }
}

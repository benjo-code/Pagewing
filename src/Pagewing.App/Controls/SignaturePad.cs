using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Pagewing.Core.Annotations;

namespace Pagewing.App.Controls;

/// <summary>
/// The drawing area of the dialog "New signature": a sheet of paper with a guide line. The mouse, a pen or a finger (while the
/// button or the contact is down) draws a stroke; <see cref="StrokeCompleted"/> hands its raw points to the dialog, which thins and
/// smooths them and gives the finished strokes back through <see cref="Strokes"/>. The ink is the colour the signature will have.
/// The paper is white whatever the theme, like a page; border and guide line come from the design tokens. The pad takes no keys
/// (there is no way to draw with a keyboard): the tab "Image" is the way for that, and the dialog's hint says so.
/// </summary>
public sealed class SignaturePad : Control
{
    /// <summary>The thickness of the ink on the pad in DIP.</summary>
    public const double InkThickness = 2.5;

    /// <summary>The fewest DIP the pointer must move for a new point of the stroke.</summary>
    private const double MinStep = 0.75;

    /// <summary>The corner radius of the sheet in DIP.</summary>
    private const double Radius = 8;

    /// <summary>The share of the height at which the guide line runs.</summary>
    private const double GuideAt = 0.72;

    public static readonly StyledProperty<IReadOnlyList<IReadOnlyList<DisplayPoint>>?> StrokesProperty =
        AvaloniaProperty.Register<SignaturePad, IReadOnlyList<IReadOnlyList<DisplayPoint>>?>(nameof(Strokes));

    public static readonly StyledProperty<Color> InkProperty =
        AvaloniaProperty.Register<SignaturePad, Color>(nameof(Ink), Colors.Black);

    private readonly List<DisplayPoint> _current = [];
    private IPointer? _pointer;

    static SignaturePad()
    {
        AffectsRender<SignaturePad>(StrokesProperty, InkProperty);
        FocusableProperty.OverrideDefaultValue<SignaturePad>(false);
        CursorProperty.OverrideDefaultValue<SignaturePad>(new Cursor(StandardCursorType.Cross));
        AutomationProperties.AccessibilityViewProperty.OverrideDefaultValue<SignaturePad>(AccessibilityView.Content);
    }

    /// <summary>Occurs when a stroke ends, with its points in pad coordinates (DIP); a dot has fewer than two.</summary>
    public event Action<IReadOnlyList<DisplayPoint>>? StrokeCompleted;

    /// <summary>Gets or sets the finished strokes to draw.</summary>
    public IReadOnlyList<IReadOnlyList<DisplayPoint>>? Strokes
    {
        get => GetValue(StrokesProperty);
        set => SetValue(StrokesProperty, value);
    }

    /// <summary>Gets or sets the colour of the ink.</summary>
    public Color Ink
    {
        get => GetValue(InkProperty);
        set => SetValue(InkProperty, value);
    }

    /// <summary>Gets whether a stroke is being drawn.</summary>
    public bool IsDrawing => _pointer is not null;

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        var border = Brush("Pw.Brush.Border.Strong");
        context.DrawRectangle(Brushes.White, border is null ? null : new Pen(border, 1), bounds.Deflate(0.5), Radius, Radius);
        if (Brush("Pw.Brush.Border.Subtle") is { } guide)
        {
            var y = bounds.Height * GuideAt;
            context.DrawLine(new Pen(guide, 1, new DashStyle([4, 4], 0)), new Point(16, y), new Point(bounds.Width - 16, y));
        }

        var ink = new SolidColorBrush(Ink);
        var pen = new Pen(ink, InkThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        foreach (var stroke in Strokes ?? [])
        {
            Draw(context, pen, stroke);
        }

        Draw(context, pen, _current);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_pointer is not null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _pointer = e.Pointer;
        e.Pointer.Capture(this);
        _current.Clear();
        _current.Add(PointOf(e));
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!ReferenceEquals(e.Pointer, _pointer))
        {
            return;
        }

        var point = PointOf(e);
        var last = _current[^1];
        if (((point.X - last.X) * (point.X - last.X)) + ((point.Y - last.Y) * (point.Y - last.Y)) >= MinStep * MinStep)
        {
            _current.Add(point);
            InvalidateVisual();
        }

        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (ReferenceEquals(e.Pointer, _pointer))
        {
            _current.Add(PointOf(e));
            e.Pointer.Capture(null);
            Finish();
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (ReferenceEquals(e.Pointer, _pointer))
        {
            // Another window or a dialog took the pointer: what is drawn so far counts.
            Finish();
        }
    }

    private void Finish()
    {
        _pointer = null;
        var points = _current.ToArray();
        _current.Clear();
        InvalidateVisual();
        StrokeCompleted?.Invoke(points);
    }

    private DisplayPoint PointOf(PointerEventArgs e)
    {
        var position = e.GetPosition(this);
        return new DisplayPoint(Math.Clamp(position.X, 0, Bounds.Width), Math.Clamp(position.Y, 0, Bounds.Height));
    }

    private IBrush? Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;

    private static void Draw(DrawingContext context, Pen pen, IReadOnlyList<DisplayPoint> points)
    {
        if (points.Count < 2)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            stream.BeginFigure(new Point(points[0].X, points[0].Y), isFilled: false);
            for (var i = 1; i < points.Count; i++)
            {
                stream.LineTo(new Point(points[i].X, points[i].Y));
            }

            stream.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, pen, geometry);
    }
}

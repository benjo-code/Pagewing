using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Pagewing.Core.Annotations;
using Pagewing.Core.Signatures;

namespace Pagewing.App.Controls;

/// <summary>
/// The small preview of a saved signature in the menu of the toolbar: its strokes in their ink, or its image, fitted into the control
/// with the aspect ratio on a white sheet with a thin border (the signature is dark ink for paper, whatever the theme). Decorative:
/// the entry that holds it carries the name.
/// </summary>
internal sealed class SignatureThumbnail : Control
{
    private IReadOnlyList<IReadOnlyList<DisplayPoint>>? _strokes;
    private Color _ink;
    private WriteableBitmap? _bitmap;
    private double _aspect = 2.5;

    static SignatureThumbnail()
    {
        IsHitTestVisibleProperty.OverrideDefaultValue<SignatureThumbnail>(false);
        FocusableProperty.OverrideDefaultValue<SignatureThumbnail>(false);
        AutomationProperties.AccessibilityViewProperty.OverrideDefaultValue<SignatureThumbnail>(AccessibilityView.Raw);
    }

    /// <summary>Shows strokes of the unit square in <paramref name="ink"/>.</summary>
    internal void SetStrokes(IReadOnlyList<IReadOnlyList<DisplayPoint>> strokes, Color ink, double aspect)
    {
        _strokes = strokes;
        _ink = ink;
        _aspect = aspect;
        _bitmap?.Dispose();
        _bitmap = null;
        InvalidateVisual();
    }

    /// <summary>Shows the pixels of an image.</summary>
    internal void SetImage(SignatureImage image)
    {
        _strokes = null;
        _bitmap?.Dispose();
        _bitmap = SignatureBitmaps.From(image);
        _aspect = (double)image.Width / image.Height;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.DrawRectangle(Brushes.White, this.TryFindResource("Pw.Brush.Border.Strong", ActualThemeVariant, out var border) && border is IBrush brush ? new Pen(brush, 1) : null, bounds.Deflate(0.5), 4, 4);
        var inner = bounds.Deflate(4);
        if (!(inner.Width > 0) || !(inner.Height > 0))
        {
            return;
        }

        var width = Math.Min(inner.Width, inner.Height * _aspect);
        var target = new Rect(inner.X + ((inner.Width - width) / 2), inner.Y + ((inner.Height - (width / _aspect)) / 2), width, width / _aspect);
        if (_bitmap is { } bitmap)
        {
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            {
                context.DrawImage(bitmap, new Rect(bitmap.Size), target);
            }

            return;
        }

        if (_strokes is not { } strokes)
        {
            return;
        }

        var pen = new Pen(new SolidColorBrush(_ink), 1.25, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        foreach (var stroke in strokes)
        {
            if (stroke.Count < 2)
            {
                continue;
            }

            var geometry = new StreamGeometry();
            using (var stream = geometry.Open())
            {
                stream.BeginFigure(new Point(target.X + (stroke[0].X * target.Width), target.Y + (stroke[0].Y * target.Height)), isFilled: false);
                for (var i = 1; i < stroke.Count; i++)
                {
                    stream.LineTo(new Point(target.X + (stroke[i].X * target.Width), target.Y + (stroke[i].Y * target.Height)));
                }

                stream.EndFigure(isClosed: false);
            }

            context.DrawGeometry(null, pen, geometry);
        }
    }
}

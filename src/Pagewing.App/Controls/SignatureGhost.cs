using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Pagewing.Core.Annotations;
using Pagewing.Core.Signatures;

namespace Pagewing.App.Controls;

/// <summary>
/// The preview of a signature that waits to be placed: follows the pointer over a page, in viewport coordinates over the pages
/// (like <see cref="DrawingOverlay"/>, which it sits beside), at the size and place the click will give it. A drawn signature shows
/// its strokes in its ink, an imported one its pixels, both half transparent, inside a dashed frame in the accent colour (so the
/// preview never rests on colour alone). It takes no input; only its own rectangle is drawn again when it moves.
/// </summary>
internal sealed class SignatureGhost : Control
{
    private const double GhostOpacity = 0.6;
    private const double FramePadding = 2;

    private Rect _destination;
    private bool _placed;
    private IReadOnlyList<IReadOnlyList<DisplayPoint>>? _strokes;
    private Color _ink;
    private double _strokeDips = 2;
    private WriteableBitmap? _bitmap;

    static SignatureGhost()
    {
        IsHitTestVisibleProperty.OverrideDefaultValue<SignatureGhost>(false);
        FocusableProperty.OverrideDefaultValue<SignatureGhost>(false);
        ClipToBoundsProperty.OverrideDefaultValue<SignatureGhost>(true);
    }

    /// <summary>Gets the rectangle in viewport DIPs the ghost is drawn in, or <see langword="default"/> while it is hidden.</summary>
    internal Rect Destination => _placed ? _destination : default;

    /// <summary>Gets whether the ghost has pixels (an imported signature) rather than strokes.</summary>
    internal bool HasBitmap => _bitmap is not null;

    /// <summary>Sets what the ghost shows: strokes in the unit square with their ink and width, or the pixels of an image.</summary>
    internal void SetContent(IReadOnlyList<IReadOnlyList<DisplayPoint>>? strokes, Color ink, double strokeDips, SignatureImage? image)
    {
        _strokes = strokes;
        _ink = ink;
        _strokeDips = Math.Max(1, strokeDips);
        _bitmap?.Dispose();
        _bitmap = image is null ? null : SignatureBitmaps.From(image);
        Hide();
    }

    /// <summary>Puts the ghost at <paramref name="destination"/> and shows it.</summary>
    internal void Place(Rect destination)
    {
        _destination = destination;
        _placed = true;
        IsVisible = true;
        InvalidateVisual();
    }

    /// <summary>Hides the ghost until it is placed again.</summary>
    internal void Hide()
    {
        _placed = false;
        IsVisible = false;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (!_placed)
        {
            return;
        }

        using (context.PushOpacity(GhostOpacity))
        {
            if (_bitmap is { } bitmap)
            {
                using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                {
                    context.DrawImage(bitmap, new Rect(bitmap.Size), _destination);
                }
            }
            else if (_strokes is { } strokes)
            {
                var pen = new Pen(new SolidColorBrush(_ink), _strokeDips, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                foreach (var stroke in strokes)
                {
                    if (stroke.Count < 2)
                    {
                        continue;
                    }

                    var geometry = new StreamGeometry();
                    using (var stream = geometry.Open())
                    {
                        stream.BeginFigure(PointOf(stroke[0]), isFilled: false);
                        for (var i = 1; i < stroke.Count; i++)
                        {
                            stream.LineTo(PointOf(stroke[i]));
                        }

                        stream.EndFigure(isClosed: false);
                    }

                    context.DrawGeometry(null, pen, geometry);
                }
            }
        }

        if (this.TryFindResource("Pw.Brush.Accent.Default", ActualThemeVariant, out var accent) && accent is IBrush frame)
        {
            context.DrawRectangle(null, new Pen(frame, 1.5, new DashStyle([4, 3], 0)), _destination.Inflate(FramePadding));
        }
    }

    private Point PointOf(DisplayPoint unit) => new(_destination.X + (unit.X * _destination.Width), _destination.Y + (unit.Y * _destination.Height));
}

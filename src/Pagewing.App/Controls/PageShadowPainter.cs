using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using RenderOptions = Avalonia.Media.RenderOptions;

namespace Pagewing.App.Controls;

/// <summary>
/// Draws the edge and shadow of a page (design system 5.3, token <c>Pw.Shadow.Page</c>) from a pre-rendered nine-patch
/// image instead of a <see cref="BoxShadows"/> on the page rectangle. The image holds what Avalonia's Skia backend
/// draws for the outer box shadows of an axis-aligned rectangle, computed on the CPU in device pixels; it is drawn 1:1
/// on the device grid without interpolation, the sides stretched from a single row or column.
/// </summary>
/// <remarks>
/// <para>
/// Why: on Windows without a hardware GPU (ANGLE over WARP) the blurred box shadow cost about 80 ms in the first frame
/// (Docs/DECISIONS.md, entry "Seitenschatten als vorberechnetes Bild (M3)"). The image costs a few microseconds of CPU
/// to build and is drawn like a tile.
/// </para>
/// <para>
/// Model of the Skia backend (Avalonia.Skia, <c>DrawingContextImpl.DrawRectangle</c>, checked against headless
/// frames): every outer shadow is the rectangle inflated by its spread and moved by its offset, blurred with a Gaussian
/// of sigma <c>0.288675 × blur + 0.5</c>, clipped (aliased) to outside the page rectangle and drawn in list order before
/// the fill. A blurred rectangle is separable: its coverage is the product of a horizontal and a vertical profile.
/// Spread, offset and blur are DIPs as in the spec; Avalonia 12.1 itself draws the offset in device pixels inside a
/// window, which differs at scalings other than 1 by a few colour levels. Inset shadows are not drawn (the page token
/// has none).
/// </para>
/// <para>
/// The left and top edge of a page lie on device pixels (<c>DocumentCanvas</c> snaps the page origin); the right and
/// bottom edge may not, so the image is made for the fractional part of the page size (in steps of 1/16 px). Images are
/// cached per shadow, scaling and size class; a large page needs one image per zoom level. The owner frees them with
/// <see cref="Clear"/>. UI thread only.
/// </para>
/// </remarks>
internal sealed class PageShadowPainter
{
    /// <summary>Steps per device pixel in which the fractional page size is matched.</summary>
    internal const int FractionSteps = 16;

    /// <summary>More cached images than this are all dropped (a zoom gesture makes one per zoom level).</summary>
    private const int MaxCachedImages = 16;

    private static readonly RenderOptions OneToOne = new() { BitmapInterpolationMode = BitmapInterpolationMode.None };

    private readonly Dictionary<PatchKey, WriteableBitmap> _patches = [];
    private BoxShadows _layerShadows;
    private double _layerScaling;
    private Layer[] _layers = [];
    private int _reachX;
    private int _reachY;

    /// <summary>Gets the number of cached images (for tests).</summary>
    internal int CachedImages => _patches.Count;

    /// <summary>
    /// Draws the shadows of a page at <paramref name="pageRect"/> (DIPs; X and Y on device pixels) before the page
    /// itself is drawn. Draws nothing for an empty page or without outer shadows.
    /// </summary>
    public void Draw(DrawingContext context, Rect pageRect, BoxShadows shadows, double renderScaling)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (pageRect.Width <= 0 || pageRect.Height <= 0 || !double.IsFinite(renderScaling) || renderScaling <= 0)
        {
            return;
        }

        if (_layerShadows != shadows || _layerScaling != renderScaling)
        {
            _layers = Layers(shadows, renderScaling);
            _reachX = Reach(_layers, horizontal: true);
            _reachY = Reach(_layers, horizontal: false);
            _layerShadows = shadows;
            _layerScaling = renderScaling;
        }

        if (_layers.Length == 0)
        {
            return;
        }

        var left = Math.Round(pageRect.X * renderScaling);
        var top = Math.Round(pageRect.Y * renderScaling);
        var horizontal = Axis.For(pageRect.Width * renderScaling, _reachX);
        var vertical = Axis.For(pageRect.Height * renderScaling, _reachY);
        var key = new PatchKey(shadows, renderScaling, horizontal.Reference, horizontal.FractionStep, vertical.Reference, vertical.FractionStep);
        if (!_patches.TryGetValue(key, out var image))
        {
            if (_patches.Count >= MaxCachedImages)
            {
                Clear();
            }

            image = CreateImage(_layers, horizontal, vertical);
            _patches.Add(key, image);
        }

        using (context.PushRenderOptions(OneToOne))
        {
            foreach (var (sourceX, destinationX, middleX) in horizontal.Segments(left))
            {
                foreach (var (sourceY, destinationY, middleY) in vertical.Segments(top))
                {
                    if (middleX && middleY)
                    {
                        // Under the page, which covers it.
                        continue;
                    }

                    context.DrawImage(
                        image,
                        new Rect(sourceX.Start, sourceY.Start, sourceX.Length, sourceY.Length),
                        new Rect(
                            destinationX.Start / renderScaling,
                            destinationY.Start / renderScaling,
                            destinationX.Length / renderScaling,
                            destinationY.Length / renderScaling));
                }
            }
        }
    }

    /// <summary>Drops the cached images (a changed theme or scaling makes new ones on the next draw).</summary>
    public void Clear()
    {
        foreach (var image in _patches.Values)
        {
            image.Dispose();
        }

        _patches.Clear();
    }

    /// <summary>The sigma Avalonia's Skia backend uses for a box shadow blur (DIPs): <c>SkBlurRadiusToSigma</c>.</summary>
    internal static double BlurSigma(double blur) => blur <= 0 ? 0 : (0.288675 * blur) + 0.5;

    /// <summary>The outer shadows in device pixels, in drawing order.</summary>
    internal static Layer[] Layers(BoxShadows shadows, double renderScaling)
    {
        var layers = new List<Layer>(shadows.Count);
        foreach (var shadow in shadows)
        {
            if (shadow.IsInset || shadow.Color.A == 0 || shadow.Equals(default(BoxShadow)))
            {
                continue;
            }

            layers.Add(new Layer(
                shadow.OffsetX * renderScaling,
                shadow.OffsetY * renderScaling,
                shadow.Spread * renderScaling,
                BlurSigma(shadow.Blur) * renderScaling,
                shadow.Color));
        }

        return [.. layers];
    }

    /// <summary>
    /// How far (whole device pixels) the shadows reach beyond a page edge along one axis, and how far inside the page
    /// their profiles still change: offset, spread and three sigma (the extent of Skia's blur kernel), plus one pixel.
    /// </summary>
    internal static int Reach(Layer[] layers, bool horizontal)
    {
        var reach = 0.0;
        foreach (var layer in layers)
        {
            var offset = Math.Abs(horizontal ? layer.OffsetX : layer.OffsetY);
            reach = Math.Max(reach, offset + Math.Abs(layer.Spread) + (3 * layer.Sigma));
        }

        return (int)Math.Ceiling(reach) + 1;
    }

    /// <summary>Coverage of a blurred (or, with sigma 0, sharp) interval [<paramref name="start"/>, <paramref name="end"/>) at device pixel <paramref name="pixel"/>.</summary>
    internal static double Profile(int pixel, double start, double end, double sigma)
    {
        if (end <= start)
        {
            return 0;
        }

        if (sigma <= 0)
        {
            return Overlap(pixel, start, end);
        }

        var centre = pixel + 0.5;
        return Math.Max(0, NormalCdf((centre - start) / sigma) - NormalCdf((centre - end) / sigma));
    }

    /// <summary>How much of the pixel [<paramref name="pixel"/>, +1) lies in [<paramref name="start"/>, <paramref name="end"/>).</summary>
    internal static double Overlap(int pixel, double start, double end) =>
        Math.Clamp(Math.Min(end, pixel + 1) - Math.Max(start, pixel), 0, 1);

    /// <summary>1 when the (aliased) clip of [<paramref name="start"/>, <paramref name="end"/>) holds pixel <paramref name="pixel"/>, else 0.</summary>
    internal static double InClip(int pixel, double start, double end) =>
        pixel >= Math.Floor(start + 0.5) && pixel < Math.Floor(end + 0.5) ? 1 : 0;

    /// <summary>The standard normal distribution function (Abramowitz and Stegun 7.1.26, error below 1.5e-7).</summary>
    internal static double NormalCdf(double x)
    {
        var z = Math.Abs(x) / Math.Sqrt(2);
        var t = 1 / (1 + (0.3275911 * z));
        var polynomial = ((((((((1.061405429 * t) - 1.453152027) * t) + 1.421413741) * t) - 0.284496736) * t) + 0.254829592) * t;
        var erf = 1 - (polynomial * Math.Exp(-z * z));
        return x >= 0 ? 0.5 * (1 + erf) : 0.5 * (1 - erf);
    }

    /// <summary>One outer shadow in device pixels.</summary>
    internal readonly record struct Layer(double OffsetX, double OffsetY, double Spread, double Sigma, Color Color);

    /// <summary>A range of whole device pixels (start and length).</summary>
    internal readonly record struct PixelRange(double Start, double Length);

    private readonly record struct PatchKey(BoxShadows Shadows, double Scaling, int ReferenceX, int FractionX, int ReferenceY, int FractionY);

    /// <summary>
    /// One axis of the nine-patch. The image holds the shadow of a reference page of <see cref="Reference"/> whole pixels
    /// plus the fraction of the real page, with <see cref="Reach"/> pixels of room on both sides. A page larger than
    /// <c>2 × Reach + 1</c> pixels is drawn from three segments: the start, one column (or row) in the middle where every
    /// profile is constant, stretched by <see cref="Stretch"/> pixels, and the end. A smaller page is drawn whole.
    /// </summary>
    internal readonly record struct Axis(int Reach, int Reference, int FractionStep, int Stretch)
    {
        public double Fraction => (double)FractionStep / FractionSteps;

        /// <summary>The width of the reference page (device pixels).</summary>
        public double ReferenceLength => Reference + Fraction;

        /// <summary>The length of the image along this axis (pixels).</summary>
        public int ImageLength => (2 * Reach) + (int)Math.Ceiling(ReferenceLength);

        public static Axis For(double pageLength, int reach)
        {
            var whole = (int)Math.Floor(pageLength);
            var step = (int)Math.Round((pageLength - whole) * FractionSteps);
            if (step == FractionSteps)
            {
                whole++;
                step = 0;
            }

            var minimum = (2 * reach) + 1;
            return whole > minimum
                ? new Axis(reach, minimum, step, whole - minimum)
                : new Axis(reach, whole, step, 0);
        }

        /// <summary>The source and destination ranges (device pixels) for a page edge at <paramref name="origin"/>.</summary>
        public IEnumerable<(PixelRange Source, PixelRange Destination, bool Middle)> Segments(double origin)
        {
            var start = origin - Reach;
            if (Stretch == 0)
            {
                yield return (new PixelRange(0, ImageLength), new PixelRange(start, ImageLength), false);
                yield break;
            }

            var split = 2 * Reach;
            yield return (new PixelRange(0, split), new PixelRange(start, split), false);
            yield return (new PixelRange(split, 1), new PixelRange(start + split, 1 + Stretch), true);
            yield return (new PixelRange(split + 1, ImageLength - split - 1), new PixelRange(start + split + 1 + Stretch, ImageLength - split - 1), false);
        }
    }

    /// <summary>Computes the premultiplied BGRA image of the shadows of the reference page (see <see cref="Axis"/>).</summary>
    internal static WriteableBitmap CreateImage(Layer[] layers, Axis horizontal, Axis vertical)
    {
        var width = horizontal.ImageLength;
        var height = vertical.ImageLength;
        var pixels = ComputePixels(layers, horizontal, vertical);
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        try
        {
            using var target = bitmap.Lock();
            unsafe
            {
                var destination = (byte*)target.Address;
                for (var row = 0; row < height; row++)
                {
                    pixels.AsSpan(row * width * 4, width * 4).CopyTo(new Span<byte>(destination + ((long)row * target.RowBytes), width * 4));
                }
            }
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }

        return bitmap;
    }

    /// <summary>The premultiplied BGRA bytes of the image, row by row without padding.</summary>
    internal static byte[] ComputePixels(Layer[] layers, Axis horizontal, Axis vertical)
    {
        var width = horizontal.ImageLength;
        var height = vertical.ImageLength;

        // The page in image coordinates, and which pixels the clip of the shadows removes. Measured against the Skia
        // backend (headless): the clip is not anti-aliased; it removes the pixels from round(start) to round(end).
        double pageLeft = horizontal.Reach, pageRight = horizontal.Reach + horizontal.ReferenceLength;
        double pageTop = vertical.Reach, pageBottom = vertical.Reach + vertical.ReferenceLength;
        var pageX = new double[width];
        var pageY = new double[height];
        for (var x = 0; x < width; x++)
        {
            pageX[x] = InClip(x, pageLeft, pageRight);
        }

        for (var y = 0; y < height; y++)
        {
            pageY[y] = InClip(y, pageTop, pageBottom);
        }

        var profilesX = new double[layers.Length][];
        var profilesY = new double[layers.Length][];
        for (var i = 0; i < layers.Length; i++)
        {
            var layer = layers[i];
            profilesX[i] = new double[width];
            profilesY[i] = new double[height];
            for (var x = 0; x < width; x++)
            {
                profilesX[i][x] = Profile(x, pageLeft - layer.Spread + layer.OffsetX, pageRight + layer.Spread + layer.OffsetX, layer.Sigma);
            }

            for (var y = 0; y < height; y++)
            {
                profilesY[i][y] = Profile(y, pageTop - layer.Spread + layer.OffsetY, pageBottom + layer.Spread + layer.OffsetY, layer.Sigma);
            }
        }

        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var outside = 1 - (pageX[x] * pageY[y]);
                if (outside <= 0)
                {
                    continue;
                }

                // Source-over of the layers in drawing order, premultiplied.
                double r = 0, g = 0, b = 0, a = 0;
                for (var i = 0; i < layers.Length; i++)
                {
                    var color = layers[i].Color;
                    var alpha = color.A / 255.0 * profilesX[i][x] * profilesY[i][y] * outside;
                    r = (color.R / 255.0 * alpha) + (r * (1 - alpha));
                    g = (color.G / 255.0 * alpha) + (g * (1 - alpha));
                    b = (color.B / 255.0 * alpha) + (b * (1 - alpha));
                    a = alpha + (a * (1 - alpha));
                }

                var offset = ((y * width) + x) * 4;
                pixels[offset] = ToByte(b);
                pixels[offset + 1] = ToByte(g);
                pixels[offset + 2] = ToByte(r);
                pixels[offset + 3] = ToByte(a);
            }
        }

        return pixels;
    }

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
}

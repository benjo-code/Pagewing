using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// The page shadow (design system 5.3, <c>Pw.Shadow.Page</c>) is drawn from a pre-rendered image
/// (<see cref="PageShadowPainter"/>, Docs/DECISIONS.md "Seitenschatten als vorberechnetes Bild (M3)").
/// </summary>
[Collection("Localization")]
public sealed class PageShadowTests
{
    /// <summary>Largest difference per colour channel to the reference (rounding and Skia's blur approximation).</summary>
    private const int Tolerance = 2;

    public static TheoryData<string, double> ThemesAndScalings => new()
    {
        { "Light", 1.0 },
        { "Light", 2.0 },
        { "Dark", 1.0 },
        { "Dark", 1.5 },
    };

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void ImageLooksLikeTheBoxShadowOfTheToken(string theme)
    {
        // Reference: Avalonia's own box shadow of the token on the page rectangle, as the page was drawn before. Only
        // at scaling 1: at other scalings the backend draws the offset in device pixels in a window and the whole
        // shadow misplaced in a RenderTargetBitmap; PixelsAroundAPageHaveTheSpecColours checks them against the spec.
        const double scaling = 1;
        var variant = Variant(theme);
        var shadows = Assert.IsType<BoxShadows>(TokenTests.Resource("Pw.Shadow.Page", variant));
        var canvas = TokenTests.Token("Surface.Canvas", variant);

        // A large page with fractional right and bottom edges (stretched sides), small pages drawn whole, a thin one.
        foreach (var page in new[] { new Rect(40, 40, 300.3, 200.7), new Rect(40, 40, 20.4, 13.2), new Rect(40, 40, 1.5, 150.25), new Rect(40, 40, 120, 90) })
        {
            using var reference = Render(page, scaling, canvas, (context, rect) => context.DrawRectangle(Brushes.White, null, new RoundedRect(rect), shadows));
            var painter = new PageShadowPainter();
            using var image = Render(page, scaling, canvas, (context, rect) =>
            {
                painter.Draw(context, rect, shadows, scaling);
                context.DrawRectangle(Brushes.White, null, rect);
            });

            var (difference, x, y) = MaxDifference(reference, image);
            Assert.True(difference <= Tolerance, string.Create(CultureInfo.InvariantCulture, $"{page} at {scaling}: {difference} at ({x}, {y})"));
            painter.Clear();
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(ThemesAndScalings))]
    public void PixelsAroundAPageHaveTheSpecColours(string theme, double scaling)
    {
        // The spec (5.3): "0 0 0 1 page.border, 0 2 10 0 page.shadow" (DIPs) over surface.canvas, the blur as a
        // Gaussian with sigma 0.288675 × 10 + 0.5 (Skia), the border first, the shadow over it, both outside the page.
        var variant = Variant(theme);
        var surface = TokenTests.Token("Surface.Canvas", variant);
        var border = TokenTests.Token("Page.Border", variant);
        var shadow = TokenTests.Token("Page.Shadow", variant);
        var (frame, page) = RenderCanvas(variant, scaling);
        using (frame)
        {
            // Device pixels; the test page lies on whole device pixels at these scalings.
            var sigma = ((0.288675 * 10) + 0.5) * scaling;
            var offset = 2 * scaling;
            var spread = 1 * scaling;
            var left = (int)Math.Round(page.X * scaling);
            var top = (int)Math.Round(page.Y * scaling);
            var bottom = (int)Math.Round(page.Bottom * scaling);
            var middleY = (int)((page.Y + (page.Height / 2)) * scaling);
            var middleX = (int)((page.X + (page.Width / 2)) * scaling);

            // Left of the page at half its height: horizontally the shadow fades out, vertically it is complete.
            for (var distance = 1; distance <= 16 * scaling; distance++)
            {
                var x = left - distance;
                var coverage = NormalCdf((x + 0.5 - left) / sigma);
                var expected = Over(Over(surface, border, Overlap(x, left - spread, left)), shadow, coverage);
                AssertNear(expected, frame.GetColor(x, middleY), $"{distance} px left of the page");
            }

            // Below the page in the middle: the shadow is moved down.
            for (var distance = 1; distance <= 18 * scaling; distance++)
            {
                var y = bottom + distance - 1;
                var coverage = NormalCdf((bottom + offset - (y + 0.5)) / sigma);
                var expected = Over(Over(surface, border, Overlap(y, bottom, bottom + spread)), shadow, coverage);
                AssertNear(expected, frame.GetColor(middleX, y), $"{distance} px below the page");
            }

            // Above the page: the border and what the moved shadow still reaches.
            for (var distance = 1; distance <= 12 * scaling; distance++)
            {
                var y = top - distance;
                var coverage = NormalCdf((y + 0.5 - (top + offset)) / sigma);
                var expected = Over(Over(surface, border, Overlap(y, top - spread, top)), shadow, coverage);
                AssertNear(expected, frame.GetColor(middleX, y), $"{distance} px above the page");
            }

            // Far from the page only the surface remains.
            AssertNear(surface, frame.GetColor(left - (int)(20 * scaling), top - (int)(20 * scaling)), "far from the page");
        }
    }

    [AvaloniaFact]
    public void PageRectangleIsNeverDrawnWithTheBoxShadows()
    {
        // An inset shadow is drawn by a box shadow of the page rectangle (inside the page) but never by the image:
        // a red frame inside the page would mean the page is drawn with BoxShadows again.
        var (frame, page) = RenderCanvas(ThemeVariant.Light, 1, BoxShadows.Parse("0 0 0 1 #14000000, inset 0 0 0 20 #FFFF0000"));
        using (frame)
        {
            for (var y = (int)page.Y; y < (int)page.Bottom; y += 3)
            {
                for (var x = (int)page.X; x < (int)page.Right; x += 3)
                {
                    var color = frame.GetColor(x, y);
                    Assert.False(color.R > 200 && color.G < 100, $"red at ({x}, {y})");
                }
            }

            // The outer part of the same value is drawn (the border left of the page).
            Assert.NotEqual(TokenTests.Token("Surface.Canvas", ThemeVariant.Light), frame.GetColor((int)page.X - 1, (int)(page.Y + 50)));
        }
    }

    [AvaloniaFact]
    public void ShadowFollowsTheThemeVariant()
    {
        var (window, canvas, page) = ShowCanvasInWindow(ThemeVariant.Light, 1);
        try
        {
            var x = (int)page.X - 1;
            var y = (int)(page.Y + (page.Height / 2));
            var light = Snapshot(window, canvas).GetColor(x, y);

            window.RequestedThemeVariant = ThemeVariant.Dark;
            Settle(window);
            var dark = Snapshot(window, canvas).GetColor(x, y);

            // The border pixel left of the page: page.border and the inner part of page.shadow over surface.canvas.
            var sigma = (0.288675 * 10) + 0.5;
            var coverage = NormalCdf(-0.5 / sigma);
            foreach (var (variant, actual) in new[] { (ThemeVariant.Light, light), (ThemeVariant.Dark, dark) })
            {
                var expected = Over(Over(TokenTests.Token("Surface.Canvas", variant), TokenTests.Token("Page.Border", variant), 1), TokenTests.Token("Page.Shadow", variant), coverage);
                AssertNear(expected, actual, variant.ToString());
            }
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void LargePagesAreDrawnFromStretchedSidesSmallPagesWhole()
    {
        var layers = PageShadowPainter.Layers(BoxShadows.Parse("0 0 0 1 #14000000, 0 2 10 0 #1A000000"), 1);
        var reach = PageShadowPainter.Reach(layers, horizontal: false);

        // Offset 2 + three sigma (10.16) → 13, plus one.
        Assert.Equal(14, reach);

        var large = PageShadowPainter.Axis.For(800.4, reach);
        Assert.Equal((2 * reach) + 1, large.Reference);
        Assert.Equal(800 - ((2 * reach) + 1), large.Stretch);
        Assert.Equal(6, large.FractionStep);

        var segments = large.Segments(100).ToList();
        Assert.Equal(3, segments.Count);
        Assert.Equal(100 - reach, segments[0].Destination.Start);
        Assert.Equal(1 + large.Stretch, segments[1].Destination.Length);
        Assert.True(segments[1].Middle);

        // The segments meet without gaps and end where the image of the page ends.
        Assert.Equal(segments[0].Destination.Start + segments[0].Destination.Length, segments[1].Destination.Start);
        Assert.Equal(segments[1].Destination.Start + segments[1].Destination.Length, segments[2].Destination.Start);
        Assert.Equal(100 + 800 + 1 + reach, segments[2].Destination.Start + segments[2].Destination.Length);

        var small = PageShadowPainter.Axis.For(20.97, reach);
        Assert.Equal(21, small.Reference);
        Assert.Equal(0, small.FractionStep);
        Assert.Equal(0, small.Stretch);
        Assert.Single(small.Segments(0));
    }

    [AvaloniaFact]
    public void ImagesAreCachedPerShadowAndScalingAndDroppedWhenTooMany()
    {
        var shadows = BoxShadows.Parse("0 0 0 1 #14000000, 0 2 10 0 #1A000000");
        var painter = new PageShadowPainter();
        using var target = new RenderTargetBitmap(new PixelSize(10, 10));
        using (var context = target.CreateDrawingContext())
        {
            painter.Draw(context, new Rect(0, 0, 400, 300), shadows, 1);
            painter.Draw(context, new Rect(0, 900, 400, 300), shadows, 1);
            Assert.Equal(1, painter.CachedImages);

            painter.Draw(context, new Rect(0, 0, 400, 300), shadows, 2);
            painter.Draw(context, new Rect(0, 0, 400, 300), BoxShadows.Parse("0 0 0 1 #24FFFFFF, 0 2 10 0 #99000000"), 1);
            Assert.Equal(3, painter.CachedImages);

            // A zoom gesture: every size a new image, the cache stays bounded.
            for (var i = 0; i < 40; i++)
            {
                painter.Draw(context, new Rect(0, 0, 400 + (i * 0.37), 300 + (i * 0.37)), shadows, 1);
            }

            Assert.InRange(painter.CachedImages, 1, 16);
        }

        painter.Clear();
        Assert.Equal(0, painter.CachedImages);
    }

    private static (FramePixels Frame, Rect Page) RenderCanvas(ThemeVariant variant, double scaling, BoxShadows? shadows = null)
    {
        var (window, canvas, page) = ShowCanvasInWindow(variant, scaling, shadows);
        try
        {
            return (Snapshot(window, canvas), page);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A canvas filling a window with one 400 × 300 DIP placeholder page (its tiles never come) on surface.canvas.</summary>
    private static (Window Window, DocumentCanvas Canvas, Rect Page) ShowCanvasInWindow(ThemeVariant variant, double scaling, BoxShadows? shadows = null)
    {
        Strings.Culture = new CultureInfo("en");
        var cache = new RenderCache();
        var scheduler = new RenderScheduler((_, _) => new TaskCompletionSource<RenderedImage>().Task, cache);
        var layout = new DocumentLayout(1, new PdfPageSize(300, 225));
        var canvas = new DocumentCanvas();
        canvas.Bind(DocumentCanvas.PlaceholderBrushProperty, canvas.GetResourceObservable("Pw.Brush.Surface.Raised"));
        if (shadows is { } fixedShadows)
        {
            canvas.PageShadow = fixedShadows;
        }
        else
        {
            canvas.Bind(DocumentCanvas.PageShadowProperty, canvas.GetResourceObservable("Pw.Shadow.Page"));
        }

        var host = new Panel { Children = { canvas } };
        host.Bind(Panel.BackgroundProperty, host.GetResourceObservable("Pw.Brush.Surface.Canvas"));
        var window = new Window { Content = host, Width = 600, Height = 500, RequestedThemeVariant = variant };
        window.Closed += (_, _) =>
        {
            canvas.Detach();
            scheduler.Dispose();
        };
        window.Show();
        window.SetRenderScaling(scaling);
        Settle(window);

        var viewport = new DipRect(-(600 - layout.ContentWidth) / 2, 0, 600, 500);
        canvas.Attach(layout, scheduler, cache);
        canvas.SetViewport(viewport, scaling);
        Settle(window);
        var rect = layout.GetPageRect(0);
        return (window, canvas, new Rect(rect.X - viewport.X, rect.Y - viewport.Y, rect.Width, rect.Height));
    }

    /// <summary>The rendered frame of the window, in device pixels (the canvas starts at its origin).</summary>
    private static FramePixels Snapshot(Window window, DocumentCanvas canvas)
    {
        Settle(window);
        Assert.Equal(new Point(0, 0), canvas.TranslatePoint(default, window));
        return new FramePixels(window.CaptureRenderedFrame()!);
    }

    private static FramePixels Render(Rect page, double scaling, Color background, Action<DrawingContext, Rect> draw)
    {
        // The page origin on device pixels, as DocumentCanvas places it.
        var rect = new Rect(Math.Round(page.X * scaling) / scaling, Math.Round(page.Y * scaling) / scaling, page.Width, page.Height);
        var size = new Size(page.Right + 40, page.Bottom + 40);
        var pixels = new PixelSize((int)Math.Ceiling(size.Width * scaling), (int)Math.Ceiling(size.Height * scaling));
        var probe = new Probe(context =>
        {
            context.FillRectangle(new SolidColorBrush(background), new Rect(size));
            draw(context, rect);
        });
        probe.Measure(size);
        probe.Arrange(new Rect(size));
        using var target = new RenderTargetBitmap(pixels, new Vector(96 * scaling, 96 * scaling));
        target.Render(probe);
        var copy = new WriteableBitmap(pixels, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var locked = copy.Lock())
        {
            target.CopyPixels(locked);
        }

        return new FramePixels(copy);
    }

    private static (int Difference, int X, int Y) MaxDifference(FramePixels a, FramePixels b)
    {
        var result = (0, 0, 0);
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                var ca = a.GetColor(x, y);
                var cb = b.GetColor(x, y);
                var difference = Math.Max(Math.Abs(ca.R - cb.R), Math.Max(Math.Abs(ca.G - cb.G), Math.Abs(ca.B - cb.B)));
                if (difference > result.Item1)
                {
                    result = (difference, x, y);
                }
            }
        }

        return result;
    }

    /// <summary>Source-over of <paramref name="color"/> (its own alpha times <paramref name="coverage"/>) on an opaque colour.</summary>
    private static (double R, double G, double B) Over((double R, double G, double B) under, Color color, double coverage)
    {
        var alpha = color.A / 255.0 * coverage;
        return ((under.R * (1 - alpha)) + (color.R * alpha), (under.G * (1 - alpha)) + (color.G * alpha), (under.B * (1 - alpha)) + (color.B * alpha));
    }

    private static (double R, double G, double B) Over(Color under, Color color, double coverage) =>
        Over((under.R, under.G, under.B), color, coverage);

    private static void AssertNear((double R, double G, double B) expected, Color actual, string where) =>
        Assert.True(
            Math.Abs(expected.R - actual.R) <= Tolerance && Math.Abs(expected.G - actual.G) <= Tolerance && Math.Abs(expected.B - actual.B) <= Tolerance,
            string.Create(CultureInfo.InvariantCulture, $"{where}: expected {expected.R:0.0},{expected.G:0.0},{expected.B:0.0}, got {actual.R},{actual.G},{actual.B}"));

    private static double Overlap(int pixel, double start, double end) => Math.Clamp(Math.Min(end, pixel + 1) - Math.Max(start, pixel), 0, 1);

    private static void AssertNear(Color expected, Color actual, string where) => AssertNear((expected.R, expected.G, expected.B), actual, where);

    /// <summary>The standard normal distribution function (series of erf, precise enough for 8-bit colours).</summary>
    private static double NormalCdf(double x)
    {
        // Simpson's rule over the density from 0 to |x|.
        const int Steps = 200;
        var z = Math.Abs(x);
        var h = z / Steps;
        var sum = 0.0;
        for (var i = 0; i <= Steps; i++)
        {
            var weight = i == 0 || i == Steps ? 1 : i % 2 == 1 ? 4 : 2;
            var t = i * h;
            sum += weight * Math.Exp(-t * t / 2);
        }

        var half = sum * h / 3 / Math.Sqrt(2 * Math.PI);
        return x >= 0 ? 0.5 + half : 0.5 - half;
    }

    private static ThemeVariant Variant(string theme) => theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.UpdateLayout();
        }
    }

    private sealed class Probe(Action<DrawingContext> draw) : Control
    {
        public override void Render(DrawingContext context) => draw(context);
    }
}

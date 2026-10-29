using Pagewing.Core.Signatures;
using Xunit;

namespace Pagewing.Core.Tests.Signatures;

public sealed class SignatureCutoutTests
{
    private static SignatureImage Image(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> pixel)
    {
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b, a) = pixel(x, y);
                var o = ((y * width) + x) * 4;
                bgra[o] = b;
                bgra[o + 1] = g;
                bgra[o + 2] = r;
                bgra[o + 3] = a;
            }
        }

        return new SignatureImage(width, height, bgra);
    }

    private static (byte R, byte G, byte B, byte A) Opaque(byte value) => (value, value, value, 255);

    private static (byte R, byte G, byte B, byte A) Rgb(byte r, byte g, byte b, byte a = 255) => (r, g, b, a);

    private static (byte R, byte G, byte B, byte A) Clear() => (255, 255, 255, 0);

    private static byte AlphaAt(SignatureImage image, int x, int y) => image.Bgra[(((y * image.Width) + x) * 4) + 3];

    private static (byte R, byte G, byte B) ColourAt(SignatureImage image, int x, int y)
    {
        var o = ((y * image.Width) + x) * 4;
        return (image.Bgra[o + 2], image.Bgra[o + 1], image.Bgra[o]);
    }

    /// <summary>Paper with a block of dark ink in the middle.</summary>
    private static SignatureImage Scan(byte paper = 240, byte ink = 30) =>
        Image(40, 20, (x, y) => x is >= 10 and < 30 && y is >= 5 and < 15 ? Opaque(ink) : Opaque(paper));

    [Fact]
    public void WhitePaperBecomesTransparent()
    {
        var image = Scan();

        var cut = SignatureCutout.Apply(image, SignatureCutout.AutoThreshold(image));

        Assert.Equal(0, AlphaAt(cut, 2, 2));
        Assert.Equal(0, AlphaAt(cut, 38, 18));
        Assert.Equal(255, AlphaAt(cut, 20, 10));
    }

    [Fact]
    public void DarkInkKeepsItsColour()
    {
        // Blue ink (luminance about 60) on paper: the pixels keep their blue.
        var image = Image(30, 10, (x, _) => x < 10 ? Rgb(20, 40, 200) : Opaque(245));

        var cut = SignatureCutout.Apply(image, SignatureCutout.AutoThreshold(image));

        Assert.Equal(255, AlphaAt(cut, 3, 3));
        Assert.Equal(((byte)20, (byte)40, (byte)200), ColourAt(cut, 3, 3));
        Assert.Equal(0, AlphaAt(cut, 20, 3));
    }

    [Fact]
    public void SoftEdgeGivesPartialAlpha()
    {
        // One pixel per luminance: threshold 128, the ramp spans 32 levels below it.
        var image = Image(5, 1, (x, _) => x switch
        {
            0 => Opaque(128),
            1 => Opaque(112),
            2 => Opaque(96),
            3 => Opaque(10),
            _ => Opaque(200),
        });

        var cut = SignatureCutout.Apply(image, 128);

        Assert.Equal(0, AlphaAt(cut, 0, 0));
        Assert.InRange(AlphaAt(cut, 1, 0), 126, 129);
        Assert.Equal(255, AlphaAt(cut, 2, 0));
        Assert.Equal(255, AlphaAt(cut, 3, 0));
        Assert.Equal(0, AlphaAt(cut, 4, 0));
    }

    [Fact]
    public void AutoThresholdSeparatesInkFromPaper()
    {
        foreach (var (paper, ink) in new (byte, byte)[] { (240, 30), (200, 90), (255, 0), (180, 60) })
        {
            var threshold = SignatureCutout.AutoThreshold(Scan(paper, ink));

            Assert.True(threshold > ink && threshold < paper, $"paper {paper}, ink {ink}: threshold {threshold}");
        }
    }

    [Fact]
    public void AllWhiteImageIsEmpty()
    {
        var white = Image(20, 10, (_, _) => Opaque(255));

        var cut = SignatureCutout.Apply(white, SignatureCutout.AutoThreshold(white));

        Assert.True(cut.IsEmpty);
        Assert.False(Scan().IsEmpty);
    }

    [Fact]
    public void AllBlackImageBecomesOneOpaqueBlockWithoutCrash()
    {
        var black = Image(20, 10, (_, _) => Opaque(0));

        var cut = SignatureCutout.TrimToContent(SignatureCutout.Apply(black, SignatureCutout.AutoThreshold(black)));

        Assert.False(cut.IsEmpty);
        Assert.Equal(20, cut.Width);
        Assert.Equal(10, cut.Height);
        Assert.Equal(255, AlphaAt(cut, 10, 5));
    }

    [Fact]
    public void TrimKeepsTwoPixelMargin()
    {
        // Content at x 10..19, y 20..24 inside 60 x 50.
        var image = Image(60, 50, (x, y) => x is >= 10 and < 20 && y is >= 20 and < 25 ? Opaque(0) : Clear());

        var trimmed = SignatureCutout.TrimToContent(image);

        Assert.Equal(14, trimmed.Width);
        Assert.Equal(9, trimmed.Height);
        Assert.Equal(255, AlphaAt(trimmed, 2, 2));
        Assert.Equal(0, AlphaAt(trimmed, 1, 1));
        Assert.Equal(255, AlphaAt(trimmed, 11, 6));
        Assert.Equal(0, AlphaAt(trimmed, 12, 7));
    }

    [Fact]
    public void TrimNeverGrowsBeyondTheImage()
    {
        var image = Image(10, 10, (x, y) => x == 0 && y == 0 ? Opaque(0) : Rgb(0, 0, 0, 0));

        var trimmed = SignatureCutout.TrimToContent(image);

        Assert.Equal(3, trimmed.Width);
        Assert.Equal(3, trimmed.Height);
        Assert.Equal(255, AlphaAt(trimmed, 0, 0));
    }

    [Fact]
    public void TrimOfAnEmptyImageReturnsItUnchanged()
    {
        var empty = Image(8, 6, (_, _) => Clear());

        Assert.Same(empty, SignatureCutout.TrimToContent(empty));
    }

    [Fact]
    public void FitWithinNeverEnlarges()
    {
        var image = Scan();

        Assert.Same(image, SignatureCutout.FitWithin(image, 1200));
        Assert.Same(image, SignatureCutout.FitWithin(image, 40));
    }

    [Fact]
    public void FitWithinKeepsAspectRatio()
    {
        var wide = Image(2400, 600, (x, _) => x < 1200 ? Opaque(0) : Clear());

        var fitted = SignatureCutout.FitWithin(wide, 1200);

        Assert.Equal(1200, fitted.Width);
        Assert.Equal(300, fitted.Height);
        Assert.Equal(255, AlphaAt(fitted, 100, 100));
        Assert.Equal(0, AlphaAt(fitted, 1100, 100));
        Assert.Equal(0, ColourAt(fitted, 100, 100).R);

        var tall = Image(500, 3000, (_, _) => Opaque(10));
        var tallFitted = SignatureCutout.FitWithin(tall, 1200);
        Assert.Equal(200, tallFitted.Width);
        Assert.Equal(1200, tallFitted.Height);
    }

    [Fact]
    public void FitWithinNeverMakesASideZero()
    {
        var sliver = Image(4000, 1, (_, _) => Opaque(0));

        var fitted = SignatureCutout.FitWithin(sliver, 1200);

        Assert.Equal(1200, fitted.Width);
        Assert.Equal(1, fitted.Height);
    }

    [Fact]
    public void ImageRejectsAWrongBufferLength()
    {
        Assert.Throws<ArgumentException>(() => new SignatureImage(2, 2, new byte[15]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SignatureImage(0, 2, []));
    }

    [Fact]
    public void PackedImageRoundTripsAndIsSmaller()
    {
        var image = Scan();

        var packed = image.Pack();
        var back = SignatureImage.Unpack(packed);

        Assert.Equal(image.Width, back.Width);
        Assert.Equal(image.Height, back.Height);
        Assert.True(image.Bgra.AsSpan().SequenceEqual(back.Bgra));
        Assert.True(packed.Deflated.Length < image.Bgra.Length);
    }
}

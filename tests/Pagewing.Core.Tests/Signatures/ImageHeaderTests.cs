using Pagewing.Core.Signatures;
using Xunit;

namespace Pagewing.Core.Tests.Signatures;

public sealed class ImageHeaderTests
{
    private static byte[] PngHead(uint width, uint height) =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
        (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
        (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height,
        8, 6, 0, 0, 0,
    ];

    private static byte[] Jpeg(byte sofMarker, int width, int height, bool withApp0 = true)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };
        if (withApp0)
        {
            bytes.AddRange([0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        }

        // A DQT segment before the frame header, and fill bytes before the marker.
        bytes.AddRange([0xFF, 0xDB, 0x00, 0x04, 0x00, 0x10]);
        bytes.AddRange([0xFF, 0xFF, 0xFF, sofMarker, 0x00, 0x11, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3]);
        bytes.AddRange(new byte[9]);
        return [.. bytes];
    }

    [Fact]
    public void ReadsPngSize()
    {
        Assert.True(ImageHeader.TryReadSize(PngHead(640, 480), out var width, out var height));
        Assert.Equal((640, 480), (width, height));
    }

    [Theory]
    [InlineData(0xC0)]
    [InlineData(0xC1)]
    [InlineData(0xC2)]
    [InlineData(0xCF)]
    public void ReadsJpegSizeOfBaselineAndProgressive(int marker)
    {
        // 0xC2 is progressive; 0xCF is the last frame marker (lossless, arithmetic): the size is read the same way.
        Assert.True(ImageHeader.TryReadSize(Jpeg((byte)marker, 1280, 720), out var width, out var height));
        Assert.Equal((1280, 720), (width, height));
        Assert.True(ImageHeader.TryReadSize(Jpeg((byte)marker, 300, 100, withApp0: false), out width, out height));
        Assert.Equal((300, 100), (width, height));
    }

    [Fact]
    public void RejectsTruncatedAndUnknownFiles()
    {
        Assert.False(ImageHeader.TryReadSize([], out _, out _));
        Assert.False(ImageHeader.TryReadSize(PngHead(10, 10).AsSpan(0, 20), out _, out _));
        Assert.False(ImageHeader.TryReadSize(Jpeg(0xC0, 10, 10).AsSpan(0, 30), out _, out _));
        Assert.False(ImageHeader.TryReadSize("GIF89a\u0001\u0000\u0001\u0000"u8.ToArray(), out _, out _));
        Assert.False(ImageHeader.TryReadSize("This is not an image at all, just text."u8.ToArray(), out _, out _));

        // DHT (0xC4), JPG (0xC8) and DAC (0xCC) are not frame headers.
        Assert.False(ImageHeader.TryReadSize(Jpeg(0xC4, 10, 10), out _, out _));
        Assert.False(ImageHeader.TryReadSize(Jpeg(0xC8, 10, 10), out _, out _));
        Assert.False(ImageHeader.TryReadSize(Jpeg(0xCC, 10, 10), out _, out _));

        // A wrong chunk name, and a segment that claims to be shorter than its own length field.
        var notIhdr = PngHead(10, 10);
        notIhdr[12] = (byte)'X';
        Assert.False(ImageHeader.TryReadSize(notIhdr, out _, out _));
        Assert.False(ImageHeader.TryReadSize([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x01, 0xFF, 0xC0, 0x00, 0x11, 8, 0, 10, 0, 10, 3], out _, out _));
    }

    [Fact]
    public void RejectsZeroSize()
    {
        Assert.False(ImageHeader.TryReadSize(PngHead(0, 10), out _, out _));
        Assert.False(ImageHeader.TryReadSize(PngHead(10, 0), out _, out _));
        Assert.False(ImageHeader.TryReadSize(Jpeg(0xC0, 0, 10), out _, out _));
        Assert.False(ImageHeader.TryReadSize(Jpeg(0xC0, 10, 0), out _, out _));
    }

    [Fact]
    public void ReadsHugeSizeWithoutDecoding()
    {
        // 60,000 x 60,000 from 29 bytes: the size is known before anything would be decoded.
        var head = PngHead(60_000, 60_000);
        Assert.Equal(29, head.Length);

        Assert.True(ImageHeader.TryReadSize(head, out var width, out var height));
        Assert.Equal((60_000, 60_000), (width, height));
    }

    [Fact]
    public void ADataTrailerAfterTheEndOfAJpegDoesNotMakeItIncomplete()
    {
        // Phones append a motion photo (an MP4) or a vendor block after the EOI; the picture itself is whole.
        byte[] scan = [0xFF, 0xDA, 0x00, 0x08, 1, 1, 0, 0, 0x3F, 0, 0x12, 0x34, 0xFF, 0x00, 0x56];
        var whole = Jpeg(0xC0, 10, 10).Concat(scan).Concat(new byte[] { 0xFF, 0xD9 }).ToArray();
        byte[] trailer = [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'m', (byte)'p', (byte)'4', (byte)'2', 7, 8, 9];

        Assert.True(ImageHeader.IsComplete([.. whole, .. trailer]));
        Assert.False(ImageHeader.IsComplete(Jpeg(0xC0, 10, 10).Concat(scan).ToArray()));
        Assert.False(ImageHeader.IsComplete(whole.AsSpan(0, whole.Length - 5)));
    }

    [Fact]
    public void DetectsAFileThatIsCutOff()
    {
        // A complete PNG ends with its IEND chunk, a complete JPEG with the marker EOI (trailing zeros are allowed).
        byte[] iend = [0, 0, 0, 0, (byte)'I', (byte)'E', (byte)'N', (byte)'D', 0xAE, 0x42, 0x60, 0x82];
        var png = PngHead(10, 10).Concat(new byte[40]).Concat(iend).ToArray();
        var jpeg = Jpeg(0xC0, 10, 10).Concat(new byte[20]).Concat(new byte[] { 0xFF, 0xD9 }).ToArray();

        Assert.True(ImageHeader.IsComplete(png));
        Assert.False(ImageHeader.IsComplete(png.AsSpan(0, png.Length - 1)));
        Assert.False(ImageHeader.IsComplete(png.AsSpan(0, png.Length / 2)));
        Assert.True(ImageHeader.IsComplete(jpeg));
        Assert.True(ImageHeader.IsComplete([.. jpeg, 0, 0, 0]));
        Assert.False(ImageHeader.IsComplete(jpeg.AsSpan(0, jpeg.Length - 1)));
        Assert.False(ImageHeader.IsComplete(Jpeg(0xC0, 10, 10)));
        Assert.False(ImageHeader.IsComplete([]));
        Assert.False(ImageHeader.IsComplete("not an image"u8.ToArray()));
    }

    [Fact]
    public void RejectsSizesBeyondTheRangeOfAnInt()
    {
        Assert.False(ImageHeader.TryReadSize(PngHead(uint.MaxValue, 10), out _, out _));
    }
}

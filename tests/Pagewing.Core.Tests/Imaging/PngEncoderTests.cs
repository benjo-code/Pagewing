using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Pagewing.Core.Imaging;
using Pagewing.Core.Pdf;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Imaging;

public sealed class PngEncoderTests
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void StartsWithPngSignatureAndIhdr()
    {
        var png = PngEncoder.Encode(Gradient(37, 23, stride: 37 * 4));

        Assert.Equal(Signature, png.AsSpan(0, 8).ToArray());
        Assert.Equal(13, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(8, 4)));
        Assert.Equal("IHDR", Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal(37, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)));
        Assert.Equal(23, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
        Assert.Equal(8, png[24]); // bit depth
        Assert.Equal(2, png[25]); // colour type RGB
        Assert.Equal(0, png[28]); // not interlaced
    }

    [Fact]
    public void RoundTripKeepsEveryPixel()
    {
        var image = Gradient(37, 23, stride: 37 * 4);

        var decoded = PngDecoder.Decode(PngEncoder.Encode(image));

        Assert.Equal(37, decoded.Width);
        Assert.Equal(23, decoded.Height);
        Assert.Equal(ExpectedRgb(image), decoded.Rgb);
    }

    [Fact]
    public void RoundTripHonoursStrideWithPadding()
    {
        var image = Gradient(11, 7, stride: (11 * 4) + 12);

        var decoded = PngDecoder.Decode(PngEncoder.Encode(image));

        Assert.Equal(ExpectedRgb(image), decoded.Rgb);
    }

    [Fact]
    public void OneByOneImageWorks()
    {
        var image = new RenderedImage(1, 1, 4, new byte[] { 10, 20, 30, 255 });

        var decoded = PngDecoder.Decode(PngEncoder.Encode(image));

        Assert.Equal((1, 1), (decoded.Width, decoded.Height));
        Assert.Equal(new byte[] { 30, 20, 10 }, decoded.Rgb);
    }

    [Fact]
    public void WideImageWorks()
    {
        var image = Gradient(3000, 2, stride: 3000 * 4);

        var decoded = PngDecoder.Decode(PngEncoder.Encode(image));

        Assert.Equal((3000, 2), (decoded.Width, decoded.Height));
        Assert.Equal(ExpectedRgb(image), decoded.Rgb);
    }

    [Fact]
    public void AlphaIsIgnored()
    {
        var opaque = Gradient(16, 9, stride: 16 * 4);
        var transparent = opaque.Pixels.ToArray();
        for (var i = 3; i < transparent.Length; i += 4)
        {
            transparent[i] = 0;
        }

        var decoded = PngDecoder.Decode(PngEncoder.Encode(new RenderedImage(16, 9, 16 * 4, transparent)));

        Assert.Equal(ExpectedRgb(opaque), decoded.Rgb);
    }

    [Fact]
    public void WritesToStreamIdenticallyToByteArray()
    {
        var image = Gradient(40, 30, stride: 40 * 4);
        using var stream = new MemoryStream();

        PngEncoder.Encode(image, stream);

        Assert.Equal(PngEncoder.Encode(image), stream.ToArray());
    }

    [Fact]
    public void LargeNoisyImageUsesSeveralIdatChunksAndRoundTrips()
    {
        const int width = 400;
        const int height = 300;
        var pixels = new byte[width * height * 4];
        new Random(1234).NextBytes(pixels);
        var image = new RenderedImage(width, height, width * 4, pixels);

        var png = PngEncoder.Encode(image);

        Assert.True(CountChunks(png, "IDAT") > 1);
        Assert.Equal(ExpectedRgb(image), PngDecoder.Decode(png).Rgb);
    }

    [Fact]
    public void WhiteA4PageAt144DpiStaysSmall()
    {
        const int width = 1191;
        const int height = 1684;
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)0xFF);

        var png = PngEncoder.Encode(new RenderedImage(width, height, width * 4, pixels));

        Assert.True(png.Length < 20 * 1024, $"A white A4 page took {png.Length} bytes.");
        var decoded = PngDecoder.Decode(png);
        Assert.Equal((width, height), (decoded.Width, decoded.Height));
        Assert.All(decoded.Rgb, b => Assert.Equal(0xFF, b));
    }

    [Fact]
    public void DecoderHandlesAllFilterTypes()
    {
        // 3x4 RGB image; the same pixels are stored once per filter type (one filter for all rows).
        const int width = 3;
        const int height = 4;
        var rgb = new byte[width * height * 3];
        for (var i = 0; i < rgb.Length; i++)
        {
            rgb[i] = (byte)((i * 37) + (i / 5 * 11));
        }

        for (var filter = 0; filter <= 4; filter++)
        {
            var png = BuildPng(width, height, colorType: 2, rgb, filter);

            var decoded = PngDecoder.Decode(png);

            Assert.Equal(rgb, decoded.Rgb);
        }
    }

    [Fact]
    public void DecoderHandlesRgbaAndDropsAlpha()
    {
        var rgba = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };

        var decoded = PngDecoder.Decode(BuildPng(3, 1, colorType: 6, rgba, filter: 1));

        Assert.Equal(new byte[] { 1, 2, 3, 5, 6, 7, 9, 10, 11 }, decoded.Rgb);
    }

    [Fact]
    public void DecoderRejectsBadSignature()
    {
        var png = PngEncoder.Encode(Gradient(4, 4, stride: 16));
        png[0] = 0;

        Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(png));
    }

    [Fact]
    public void DecoderRejectsCorruptCrc()
    {
        var png = PngEncoder.Encode(Gradient(4, 4, stride: 16));
        png[20] ^= 0xFF; // inside the IHDR data

        Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(png));
    }

    [Fact]
    public void DecoderRejectsInterlacedImages()
    {
        var png = PngEncoder.Encode(Gradient(4, 4, stride: 16));
        png[28] = 1;
        FixCrc(png, chunkStart: 8);

        Assert.Throws<NotSupportedException>(() => PngDecoder.Decode(png));
    }

    private static RenderedImage Gradient(int width, int height, int stride)
    {
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = (y * stride) + (x * 4);
                pixels[o] = (byte)((x * 7) + (y * 3)); // blue
                pixels[o + 1] = (byte)((x * 255 / Math.Max(1, width - 1)) ^ y); // green
                pixels[o + 2] = (byte)(255 - (y * 9) - x); // red
                pixels[o + 3] = 0xFF;
            }
        }

        return new RenderedImage(width, height, stride, pixels);
    }

    private static byte[] ExpectedRgb(RenderedImage image)
    {
        var source = image.Pixels.Span;
        var rgb = new byte[image.Width * image.Height * 3];
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var s = (y * image.Stride) + (x * 4);
                var d = ((y * image.Width) + x) * 3;
                rgb[d] = source[s + 2];
                rgb[d + 1] = source[s + 1];
                rgb[d + 2] = source[s];
            }
        }

        return rgb;
    }

    private static int CountChunks(byte[] png, string type)
    {
        var count = 0;
        var offset = 8;
        while (offset + 8 <= png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            if (Encoding.ASCII.GetString(png, offset + 4, 4) == type)
            {
                count++;
            }

            offset += 12 + length;
        }

        return count;
    }

    /// <summary>Builds a PNG with the given pixels (RGB or RGBA, tightly packed), every row using <paramref name="filter"/>.</summary>
    private static byte[] BuildPng(int width, int height, int colorType, byte[] pixels, int filter)
    {
        var bpp = colorType == 2 ? 3 : 4;
        var rowLength = width * bpp;
        var raw = new byte[height * (rowLength + 1)];
        for (var y = 0; y < height; y++)
        {
            raw[y * (rowLength + 1)] = (byte)filter;
            for (var i = 0; i < rowLength; i++)
            {
                int cur = pixels[(y * rowLength) + i];
                int a = i >= bpp ? pixels[(y * rowLength) + i - bpp] : 0;
                int b = y > 0 ? pixels[((y - 1) * rowLength) + i] : 0;
                int c = i >= bpp && y > 0 ? pixels[((y - 1) * rowLength) + i - bpp] : 0;
                var predictor = filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    _ => Paeth(a, b, c),
                };
                raw[(y * (rowLength + 1)) + 1 + i] = (byte)(cur - predictor);
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        using var png = new MemoryStream();
        png.Write(Signature);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8;
        ihdr[9] = (byte)colorType;
        WriteChunk(png, "IHDR", ihdr);
        var data = compressed.ToArray();
        var half = data.Length / 2; // two IDAT chunks
        WriteChunk(png, "IDAT", data.AsSpan(0, half));
        WriteChunk(png, "IDAT", data.AsSpan(half));
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        stream.Write(number);
        var body = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type, body);
        data.CopyTo(body.AsSpan(4));
        stream.Write(body);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(body));
        stream.Write(number);
    }

    private static void FixCrc(byte[] png, int chunkStart)
    {
        var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(chunkStart, 4));
        var crc = Crc32(png.AsSpan(chunkStart + 4, 4 + length));
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(chunkStart + 8 + length, 4), crc);
    }

    /// <summary>Bitwise CRC-32, independent of the implementation under test.</summary>
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}

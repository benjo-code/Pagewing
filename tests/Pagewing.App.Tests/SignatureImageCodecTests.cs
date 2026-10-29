using System.IO.Compression;
using Avalonia.Headless.XUnit;
using Pagewing.App.Services;
using Pagewing.Core.Signatures;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>Decoding and encoding the image of a signature (milestone 12, task 9): PNG and JPEG in, a PNG with alpha out, nothing on the UI thread.</summary>
public sealed class SignatureImageCodecTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SignatureImage Gradient(int width, int height, bool soft)
    {
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = ((y * width) + x) * 4;
                bgra[o] = (byte)(200 - (x * 2));
                bgra[o + 1] = (byte)(40 + y);
                bgra[o + 2] = (byte)(30 + (x * 3));
                bgra[o + 3] = soft ? (byte)(x < width / 2 ? 255 : x % 2 == 0 ? 0 : 255) : (byte)255;
            }
        }

        return new SignatureImage(width, height, bgra);
    }

    private static byte[] PngHead(uint width, uint height) =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
        (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
        (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height,
        8, 6, 0, 0, 0,
    ];

    private static async Task<SignatureImageProblem> ProblemOfAsync(byte[] file, Func<byte[], SignatureImage>? decoder = null)
    {
        var failure = await Assert.ThrowsAsync<SignatureImageException>(() => SignatureImageCodec.DecodeAsync(file, Ct, decoder));
        return failure.Reason;
    }

    /// <summary>A PNG of grey values (colour type 0), as a scanner in grey or black and white mode writes it.</summary>
    private static byte[] GreyPng(int width, int height, int bitDepth, Func<int, int, int> value)
    {
        var bytesPerPixel = bitDepth / 8;
        using var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            for (var x = 0; x < width; x++)
            {
                var v = value(x, y);
                if (bytesPerPixel == 2)
                {
                    raw.WriteByte((byte)(v >> 8));
                }

                raw.WriteByte((byte)v);
            }
        }

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(raw.ToArray());
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", [(byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width, (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height, (byte)bitDepth, 0, 0, 0, 0]);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        byte[] length = [(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length];
        output.Write(length);
        var body = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(body, 0);
        data.CopyTo(body, 4);
        output.Write(body);
        var crc = 0xFFFFFFFFu;
        foreach (var b in body)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        crc ^= 0xFFFFFFFFu;
        output.Write([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
    }

    [AvaloniaTheory]
    [InlineData(8)]
    [InlineData(16)]
    public async Task GreyPngIsReadAsOpaqueGreyPixels(int bitDepth)
    {
        var max = bitDepth == 8 ? 255 : 65535;
        var png = GreyPng(40, 20, bitDepth, (x, _) => x < 20 ? 0 : max);

        var image = await SignatureImageCodec.DecodeAsync(png, Ct);

        Assert.Equal((40, 20), (image.Width, image.Height));
        Assert.Equal((0, 0, 0, 255), (image.Bgra[0], image.Bgra[1], image.Bgra[2], image.Bgra[3]));
        var white = (30 * 4);
        Assert.Equal((255, 255, 255, 255), (image.Bgra[white], image.Bgra[white + 1], image.Bgra[white + 2], image.Bgra[white + 3]));
    }

    [AvaloniaFact]
    public async Task PngRoundTripKeepsPixelsAndAlpha()
    {
        var image = Gradient(64, 24, soft: true);

        var png = SignatureImageCodec.EncodePng(image);
        var back = await SignatureImageCodec.DecodeAsync(png, Ct);

        Assert.True(ImageHeader.TryReadSize(png, out var width, out var height));
        Assert.Equal((64, 24), (width, height));
        Assert.Equal((64, 24), (back.Width, back.Height));
        for (var i = 0; i < image.Bgra.Length; i += 4)
        {
            Assert.Equal(image.Bgra[i + 3], back.Bgra[i + 3]);
            if (image.Bgra[i + 3] == 255)
            {
                Assert.Equal(image.Bgra[i], back.Bgra[i]);
                Assert.Equal(image.Bgra[i + 1], back.Bgra[i + 1]);
                Assert.Equal(image.Bgra[i + 2], back.Bgra[i + 2]);
            }
        }
    }

    [AvaloniaFact]
    public async Task PremultipliedAlphaIsUndone()
    {
        // A half transparent pixel of a clear colour comes back with that colour, not darkened by the premultiplication.
        var bgra = new byte[4 * 4 * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = 40;
            bgra[i + 1] = 80;
            bgra[i + 2] = 200;
            bgra[i + 3] = 128;
        }

        var back = await SignatureImageCodec.DecodeAsync(SignatureImageCodec.EncodePng(new SignatureImage(4, 4, bgra)), Ct);

        Assert.Equal(128, back.Bgra[3]);
        Assert.InRange(back.Bgra[0], 39, 41);
        Assert.InRange(back.Bgra[1], 79, 81);
        Assert.InRange(back.Bgra[2], 199, 201);
    }

    [AvaloniaFact]
    public async Task TooLargeFileIsRefusedWithoutDecoding()
    {
        var file = new byte[SignatureLimits.MaxFileBytes + 1];
        PngHead(10, 10).CopyTo(file, 0);
        var decoded = false;

        var problem = await ProblemOfAsync(file, bytes =>
        {
            decoded = true;
            return Gradient(2, 2, soft: false);
        });

        Assert.Equal(SignatureImageProblem.TooLarge, problem);
        Assert.False(decoded);
    }

    [AvaloniaFact]
    public async Task HugeDimensionsAreRefusedBeforeDecoding()
    {
        var decoded = false;

        var problem = await ProblemOfAsync(PngHead(60_000, 60_000), bytes =>
        {
            decoded = true;
            return Gradient(2, 2, soft: false);
        });
        var edge = await ProblemOfAsync(PngHead(SignatureLimits.MaxSourceSide + 1, 10));

        Assert.Equal(SignatureImageProblem.TooManyPixels, problem);
        Assert.Equal(SignatureImageProblem.TooManyPixels, edge);
        Assert.False(decoded);
    }

    [AvaloniaFact]
    public async Task JpegNamedPngIsStillRead()
    {
        // The content decides, never the name: a JPEG is decoded whatever the caller called it.
        var jpeg = EncodeJpeg(Gradient(48, 20, soft: false));

        var back = await SignatureImageCodec.DecodeAsync(jpeg, Ct);

        Assert.Equal((48, 20), (back.Width, back.Height));
        Assert.True(back.Bgra[3] == 255);
        Assert.False(back.IsEmpty);
    }

    [AvaloniaFact]
    public async Task TruncatedFileIsUnreadable()
    {
        var png = SignatureImageCodec.EncodePng(Gradient(64, 24, soft: true));

        Assert.Equal(SignatureImageProblem.Unreadable, await ProblemOfAsync(png.AsSpan(0, png.Length / 2).ToArray()));
        Assert.Equal(SignatureImageProblem.Unreadable, await ProblemOfAsync(png.AsSpan(0, 20).ToArray()));
        Assert.Equal(SignatureImageProblem.Unreadable, await ProblemOfAsync("This is not an image at all, just text."u8.ToArray()));
        Assert.Equal(SignatureImageProblem.Unreadable, await ProblemOfAsync("GIF89a\u0001\u0000\u0001\u0000"u8.ToArray()));
    }

    [AvaloniaFact]
    public async Task ZeroByteFileIsUnreadable()
    {
        Assert.Equal(SignatureImageProblem.Unreadable, await ProblemOfAsync([]));
    }

    [AvaloniaFact]
    public async Task ATransparentImageHasNothingToSee()
    {
        var clear = SignatureImageCodec.EncodePng(new SignatureImage(8, 8, new byte[8 * 8 * 4]));

        Assert.Equal(SignatureImageProblem.Empty, await ProblemOfAsync(clear));
    }

    [AvaloniaFact]
    public async Task DoesNotRunOnTheUiThread()
    {
        var caller = Environment.CurrentManagedThreadId;
        var seen = -1;
        var png = SignatureImageCodec.EncodePng(Gradient(16, 8, soft: false));

        await SignatureImageCodec.DecodeAsync(png, Ct, bytes =>
        {
            seen = Environment.CurrentManagedThreadId;
            return SignatureImageCodec.DecodeCore(bytes);
        });

        Assert.NotEqual(-1, seen);
        Assert.NotEqual(caller, seen);
    }

    [AvaloniaFact]
    public async Task ACanceledDecodeIsCanceled()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SignatureImageCodec.DecodeAsync(SignatureImageCodec.EncodePng(Gradient(16, 8, soft: false)), cancel.Token));
    }

    private static byte[] EncodeJpeg(SignatureImage image)
    {
        using var bitmap = new SkiaSharp.SKBitmap(image.Width, image.Height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul);
        System.Runtime.InteropServices.Marshal.Copy(image.Bgra, 0, bitmap.GetPixels(), image.Bgra.Length);
        using var encoded = SkiaSharp.SKImage.FromBitmap(bitmap).Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90);
        return encoded.ToArray();
    }
}

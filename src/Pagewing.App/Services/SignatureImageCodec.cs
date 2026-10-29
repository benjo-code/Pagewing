using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Pagewing.Core.Signatures;

namespace Pagewing.App.Services;

/// <summary>Why the image of a signature was refused.</summary>
internal enum SignatureImageProblem
{
    /// <summary>The file is larger than <see cref="SignatureLimits.MaxFileBytes"/>.</summary>
    TooLarge,

    /// <summary>A side of the image is longer than <see cref="SignatureLimits.MaxSourceSide"/> pixels.</summary>
    TooManyPixels,

    /// <summary>The file is no PNG or JPEG that can be read (empty, cut off, another format, damaged).</summary>
    Unreadable,

    /// <summary>The image is fully transparent: there is nothing to see.</summary>
    Empty,
}

/// <summary>An image of a signature could not be used; <see cref="Reason"/> says why (the message carries no content of the file).</summary>
internal sealed class SignatureImageException(SignatureImageProblem reason) : Exception("The image cannot be used as a signature: " + reason)
{
    /// <summary>Gets why the image was refused.</summary>
    public SignatureImageProblem Reason { get; } = reason;
}

/// <summary>
/// Reads the file of a signature image (PNG or JPEG, whatever its name) into a <see cref="SignatureImage"/> and writes one as a PNG
/// with alpha. The codec is Avalonia's (Skia): no new dependency. The file's size and its pixel size (from the head, before
/// anything is decoded) are checked first, so a file of 60,000 x 60,000 pixels never reaches the decoder. Decoding runs on a pool
/// thread, never on the UI thread and never on the PDFium thread.
/// </summary>
internal static class SignatureImageCodec
{
    /// <summary>
    /// Reads <paramref name="file"/>. <paramref name="decoder"/> replaces the decoding step (tests watch its thread); it only ever
    /// sees a file whose size and pixel size passed the checks.
    /// </summary>
    /// <exception cref="SignatureImageException">The file is too large, has too many pixels, cannot be read, or shows nothing.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static async Task<SignatureImage> DecodeAsync(byte[] file, CancellationToken cancellationToken, Func<byte[], SignatureImage>? decoder = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        cancellationToken.ThrowIfCancellationRequested();
        if (file.Length > SignatureLimits.MaxFileBytes)
        {
            throw new SignatureImageException(SignatureImageProblem.TooLarge);
        }

        if (!ImageHeader.TryReadSize(file, out var width, out var height))
        {
            throw new SignatureImageException(SignatureImageProblem.Unreadable);
        }

        if (width > SignatureLimits.MaxSourceSide || height > SignatureLimits.MaxSourceSide)
        {
            throw new SignatureImageException(SignatureImageProblem.TooManyPixels);
        }

        // A file cut off would decode to an image with missing rows: it is refused (the decoder does not say).
        if (!ImageHeader.IsComplete(file))
        {
            throw new SignatureImageException(SignatureImageProblem.Unreadable);
        }

        SignatureImage image;
        try
        {
            image = await Task.Run(() => (decoder ?? DecodeCore)(file), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (SignatureImageException or OperationCanceledException or OutOfMemoryException))
        {
            // Whatever a damaged file does to the decoder, the user hears "cannot be read"; nothing of the file goes into the message.
            throw new SignatureImageException(SignatureImageProblem.Unreadable);
        }

        return image.IsEmpty ? throw new SignatureImageException(SignatureImageProblem.Empty) : image;
    }

    /// <summary>Decodes a PNG or JPEG into unpremultiplied BGRA. Blocking: call it on a pool thread.</summary>
    /// <exception cref="SignatureImageException">The decoder gave a pixel format this code does not read.</exception>
    internal static unsafe SignatureImage DecodeCore(byte[] file)
    {
        using var stream = new MemoryStream(file, writable: false);
        using var bitmap = new Bitmap(stream);
        var size = bitmap.PixelSize;
        var format = bitmap.Format;
        if (format is not { } pixelFormat || (pixelFormat != PixelFormat.Bgra8888 && pixelFormat != PixelFormat.Rgba8888))
        {
            throw new SignatureImageException(SignatureImageProblem.Unreadable);
        }

        var bgra = new byte[checked(size.Width * size.Height * 4)];
        fixed (byte* pixels = bgra)
        {
            bitmap.CopyPixels(new PixelRect(size), (nint)pixels, bgra.Length, size.Width * 4);
        }

        var premultiplied = bitmap.AlphaFormat == AlphaFormat.Premul;
        var swap = pixelFormat == PixelFormat.Rgba8888;
        for (var i = 0; i < bgra.Length; i += 4)
        {
            if (swap)
            {
                (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
            }

            var alpha = bgra[i + 3];
            if (premultiplied && alpha is > 0 and < 255)
            {
                bgra[i] = Unpremultiplied(bgra[i], alpha);
                bgra[i + 1] = Unpremultiplied(bgra[i + 1], alpha);
                bgra[i + 2] = Unpremultiplied(bgra[i + 2], alpha);
            }
        }

        return new SignatureImage(size.Width, size.Height, bgra);
    }

    /// <summary>Writes the image as a PNG (lossless, with alpha).</summary>
    public static byte[] EncodePng(SignatureImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        using var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (var locked = bitmap.Lock())
        {
            for (var y = 0; y < image.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(image.Bgra, y * image.Width * 4, locked.Address + (y * locked.RowBytes), image.Width * 4);
            }
        }

        using var output = new MemoryStream();
        bitmap.Save(output, PngBitmapEncoderOptions.Default);
        return output.ToArray();
    }

    private static byte Unpremultiplied(byte channel, byte alpha) => (byte)Math.Min(255, ((channel * 255) + (alpha / 2)) / alpha);
}

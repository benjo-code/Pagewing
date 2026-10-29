using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Pagewing.Core.Signatures;

namespace Pagewing.App.Controls;

/// <summary>Makes the bitmap a signature image is drawn from (not premultiplied alpha, as the pixels of <see cref="SignatureImage"/> are).</summary>
internal static class SignatureBitmaps
{
    /// <summary>Copies the pixels into a new bitmap; the caller disposes it.</summary>
    public static WriteableBitmap From(SignatureImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var locked = bitmap.Lock();
        for (var y = 0; y < image.Height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(image.Bgra, y * image.Width * 4, locked.Address + (y * locked.RowBytes), image.Width * 4);
        }

        return bitmap;
    }
}

using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;

namespace Pagewing.App.Controls;

/// <summary>
/// The bitmaps the document canvas draws, one per rendered key (preview or sharp tile). Holds only what is wanted
/// at the moment: <see cref="Retain"/> disposes every other bitmap, so the copies never outgrow what the viewport
/// needs (the render cache keeps the pixels for later). The owner frees the rest with <see cref="Clear"/>. UI thread only.
/// </summary>
internal sealed class TileBitmaps
{
    private readonly Dictionary<TileKey, Bitmap> _bitmaps = [];

    /// <summary>Gets the number of bitmaps held.</summary>
    public int Count => _bitmaps.Count;

    /// <summary>Gets the keys of the bitmaps held.</summary>
    public Dictionary<TileKey, Bitmap>.KeyCollection Keys => _bitmaps.Keys;

    /// <summary>Creates an opaque BGRA bitmap with the pixels of <paramref name="image"/>, copied row by row (the strides may differ).</summary>
    public static WriteableBitmap Create(RenderedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        try
        {
            using var target = bitmap.Lock();
            var rowLength = image.Width * 4;
            var source = image.Pixels.Span;
            unsafe
            {
                var destination = (byte*)target.Address;
                for (var row = 0; row < image.Height; row++)
                {
                    source.Slice(row * image.Stride, rowLength).CopyTo(new Span<byte>(destination + ((long)row * target.RowBytes), rowLength));
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

    public bool Contains(TileKey key) => _bitmaps.ContainsKey(key);

    public bool TryGet(TileKey key, out Bitmap bitmap) => _bitmaps.TryGetValue(key, out bitmap!);

    /// <summary>Stores <paramref name="bitmap"/> under <paramref name="key"/>; a bitmap already stored there is disposed.</summary>
    public void Add(TileKey key, Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (_bitmaps.Remove(key, out var old) && !ReferenceEquals(old, bitmap))
        {
            old.Dispose();
        }

        _bitmaps[key] = bitmap;
    }

    /// <summary>Disposes and drops every bitmap whose key is not in <paramref name="wanted"/>.</summary>
    public void Retain(IReadOnlySet<TileKey> wanted)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        List<TileKey>? unwanted = null;
        foreach (var key in _bitmaps.Keys)
        {
            if (!wanted.Contains(key))
            {
                (unwanted ??= []).Add(key);
            }
        }

        if (unwanted is null)
        {
            return;
        }

        foreach (var key in unwanted)
        {
            if (_bitmaps.Remove(key, out var bitmap))
            {
                bitmap.Dispose();
            }
        }
    }

    /// <summary>Disposes and drops all bitmaps.</summary>
    public void Clear()
    {
        foreach (var bitmap in _bitmaps.Values)
        {
            bitmap.Dispose();
        }

        _bitmaps.Clear();
    }
}

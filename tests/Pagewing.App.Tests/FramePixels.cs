using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Pagewing.App.Tests;

/// <summary>Reads pixels of a frame captured from a headless window (<c>CaptureRenderedFrame</c>).</summary>
internal sealed class FramePixels : IDisposable
{
    private readonly WriteableBitmap _frame;
    private readonly byte[] _pixels;
    private readonly int _rowBytes;
    private readonly bool _blueFirst;

    public FramePixels(WriteableBitmap frame)
    {
        _frame = frame;
        _blueFirst = frame.Format != PixelFormat.Rgba8888;
        using var locked = frame.Lock();
        _rowBytes = locked.RowBytes;
        _pixels = new byte[locked.RowBytes * locked.Size.Height];
        Marshal.Copy(locked.Address, _pixels, 0, _pixels.Length);
    }

    /// <summary>Renders <paramref name="visual"/> on its own (no dispatcher jobs run, unlike a window capture) and reads the pixels.</summary>
    public static FramePixels Snapshot(Visual visual)
    {
        var size = new PixelSize((int)Math.Ceiling(visual.Bounds.Width), (int)Math.Ceiling(visual.Bounds.Height));
        using var rendered = new RenderTargetBitmap(size);
        rendered.Render(visual);
        var copy = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var target = copy.Lock())
        {
            rendered.CopyPixels(target);
        }

        return new FramePixels(copy);
    }

    public int Width => _frame.PixelSize.Width;

    public int Height => _frame.PixelSize.Height;

    public void Dispose() => _frame.Dispose();

    /// <summary>The pixel colour (premultiplied alpha is irrelevant for the opaque frames used here).</summary>
    public Color GetColor(int x, int y)
    {
        var offset = (y * _rowBytes) + (x * 4);
        var first = _pixels[offset];
        var second = _pixels[offset + 1];
        var third = _pixels[offset + 2];
        return _blueFirst ? Color.FromRgb(third, second, first) : Color.FromRgb(first, second, third);
    }

    /// <summary>A dark pixel, as drawn by black icons on a white background.</summary>
    public bool IsInked(int x, int y)
    {
        var color = GetColor(x, y);
        return Math.Max(color.R, Math.Max(color.G, color.B)) < 96;
    }

    /// <summary>A pixel that is exactly white.</summary>
    public bool IsBlank(int x, int y) => GetColor(x, y) == Colors.White;
}

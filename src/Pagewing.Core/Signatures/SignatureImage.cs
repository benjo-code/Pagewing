using System.IO.Compression;

namespace Pagewing.Core.Signatures;

/// <summary>
/// The pixels of a signature image: BGRA, not premultiplied (the colour of a pixel does not depend on its alpha), top row first,
/// <see cref="Bgra"/>.Length = Width * Height * 4.
/// </summary>
public sealed record SignatureImage
{
    /// <summary>The most pixels an image may have (<see cref="SignatureLimits.MaxSourceSide"/> squared).</summary>
    internal const long MaxPixels = (long)SignatureLimits.MaxSourceSide * SignatureLimits.MaxSourceSide;

    /// <summary>The largest alpha that still counts as no ink at all (anti-aliasing noise).</summary>
    internal const byte NegligibleAlpha = 8;

    /// <summary>Creates an image over <paramref name="bgra"/> (not copied).</summary>
    /// <exception cref="ArgumentOutOfRangeException">A size is not positive or the image has more than <see cref="MaxPixels"/> pixels.</exception>
    /// <exception cref="ArgumentException">The buffer does not have exactly Width * Height * 4 bytes.</exception>
    public SignatureImage(int width, int height, byte[] bgra)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((long)width * height, MaxPixels, nameof(width));
        if (bgra.Length != (long)width * height * 4)
        {
            throw new ArgumentException("The buffer must hold Width * Height * 4 bytes.", nameof(bgra));
        }

        Width = width;
        Height = height;
        Bgra = bgra;
    }

    /// <summary>Gets the width in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the height in pixels.</summary>
    public int Height { get; }

    /// <summary>Gets the pixels (BGRA, not premultiplied).</summary>
    public byte[] Bgra { get; }

    /// <summary>Gets whether no pixel has more than a negligible alpha: there is nothing to see.</summary>
    public bool IsEmpty
    {
        get
        {
            for (var i = 3; i < Bgra.Length; i += 4)
            {
                if (Bgra[i] > NegligibleAlpha)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Compresses the pixels (Deflate) for the editing history.</summary>
    public PackedSignatureImage Pack()
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(Bgra);
        }

        return new PackedSignatureImage(Width, Height, output.ToArray());
    }

    /// <summary>Restores the pixels of a packed image.</summary>
    /// <exception cref="InvalidDataException">The data does not decompress to exactly Width * Height * 4 bytes.</exception>
    public static SignatureImage Unpack(PackedSignatureImage packed)
    {
        ArgumentNullException.ThrowIfNull(packed);
        var length = (long)packed.Width * packed.Height * 4;
        if (packed.Width <= 0 || packed.Height <= 0 || length > MaxPixels * 4)
        {
            throw new InvalidDataException("The packed image has an invalid size.");
        }

        var bgra = new byte[length];
        using var input = new MemoryStream(packed.Deflated);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        deflate.ReadExactly(bgra);
        if (deflate.ReadByte() != -1)
        {
            throw new InvalidDataException("The packed image is longer than its size says.");
        }

        return new SignatureImage(packed.Width, packed.Height, bgra);
    }
}

/// <summary>A <see cref="SignatureImage"/> compressed with Deflate: what the editing history keeps.</summary>
/// <param name="Width">The width in pixels.</param>
/// <param name="Height">The height in pixels.</param>
/// <param name="Deflated">The Deflate stream of the BGRA pixels.</param>
public sealed record PackedSignatureImage(int Width, int Height, byte[] Deflated);

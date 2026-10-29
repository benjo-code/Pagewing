namespace Pagewing.Core.Signatures;

/// <summary>The limits of the signature feature: what a hostile or merely large input may cost.</summary>
public static class SignatureLimits
{
    /// <summary>The most signatures the library keeps.</summary>
    public const int MaxSignatures = 10;

    /// <summary>The largest image file that is imported, in bytes.</summary>
    public const int MaxFileBytes = 10 * 1024 * 1024;

    /// <summary>The longest side of an imported image in pixels, read from the head of the file before decoding.</summary>
    public const int MaxSourceSide = 6000;

    /// <summary>The longest side of a stored signature image in pixels; larger images are scaled down.</summary>
    public const int MaxStoredSide = 1200;

    /// <summary>The width of a signature when it is first placed, in points (at most <see cref="MaxStartWidthFraction"/> of the page).</summary>
    public const double StartWidthPoints = 160;

    /// <summary>The largest share of the page width a signature has when it is first placed.</summary>
    public const double MaxStartWidthFraction = 1.0 / 3;

    /// <summary>The most memory the packed images of one editing history may use, in bytes.</summary>
    public const int MaxHistoryImageBytes = 32 * 1024 * 1024;
}

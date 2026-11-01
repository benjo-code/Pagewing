namespace Pagewing.Core.Redaction;

/// <summary>The limits of redaction planning and checking (starting values, pinned by a test).</summary>
public static class RedactionLimits
{
    /// <summary>The most marks one <see cref="RedactionPlan"/> may hold; checked before anything is allocated.</summary>
    public const int MaxMarks = 10_000;

    /// <summary>The most rectangles one <see cref="RedactionMark"/> may have (a long selection has one per line).</summary>
    public const int MaxAreasPerMark = 500;

    /// <summary>The longest term, in UTF-16 code units, that a mark keeps for the check.</summary>
    public const int MaxTermLength = 1_000;

    /// <summary>The shortest removed text, in characters, that the raw-bytes check uses; shorter text would match streams by chance.</summary>
    public const int MinRawCheckLength = 4;

    /// <summary>The most bytes one stream may unpack to in the raw-bytes check (a larger stream is a decompression bomb and is reported).</summary>
    public const long MaxInflatedStream = 128L * 1024 * 1024;

    /// <summary>The most bytes all streams of one file may unpack to in the raw-bytes check.</summary>
    public const long MaxInflatedTotal = 1024L * 1024 * 1024;
}

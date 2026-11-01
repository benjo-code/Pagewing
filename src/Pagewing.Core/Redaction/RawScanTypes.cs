using System.Globalization;

namespace Pagewing.Core.Redaction;

/// <summary>The limits of the raw-bytes scan against decompression bombs.</summary>
/// <param name="MaxInflatedStream">The most bytes one stream may unpack to; a stream that grows past it is scanned up to the limit and reported as unreadable.</param>
/// <param name="MaxInflatedTotal">The most bytes all streams of one file may unpack to together; streams after it are not unpacked and are reported.</param>
public sealed record RawScanLimits(long MaxInflatedStream, long MaxInflatedTotal)
{
    /// <summary>Gets the limits of <see cref="RedactionLimits"/>.</summary>
    public static RawScanLimits Default { get; } = new(RedactionLimits.MaxInflatedStream, RedactionLimits.MaxInflatedTotal);
}

/// <summary>What the raw-bytes scan could not (fully) read.</summary>
public enum RawScanNoteKind
{
    /// <summary>A stream uses a filter Pagewing cannot unpack; its bytes were searched as they are.</summary>
    UnsupportedFilter,

    /// <summary>Image data: searched as bytes only, never unpacked (an image holds pixels, not text).</summary>
    ImageData,

    /// <summary>A stream is damaged: what unpacked was searched, the rest is unknown.</summary>
    UnreadableStream,

    /// <summary>A stream unpacks to more than the limit of one stream; the part up to the limit was searched.</summary>
    StreamTooLarge,

    /// <summary>The streams of the file unpack to more than the limit of all streams together; later streams were not unpacked.</summary>
    TotalLimitReached,

    /// <summary>A stream uses a predictor, which is not undone: the unpacked bytes were searched as they are.</summary>
    Predictor,

    /// <summary>The file has an <c>/Encrypt</c> entry: its streams are not readable.</summary>
    Encrypted,

    /// <summary>A text was found so often that matches were counted without keeping their positions.</summary>
    TooManyMatches,
}

/// <summary>One thing the raw-bytes scan could not (fully) read, aggregated over the streams it concerns.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Detail">A short English explanation (the filter name for <see cref="RawScanNoteKind.UnsupportedFilter"/>).</param>
/// <param name="CouldHideText">Whether text could hide in what was not read (an unreadable stream, an unknown filter); image data cannot.</param>
/// <param name="Count">How many streams it concerns.</param>
/// <param name="FirstOffset">The file offset of the first of them (the start of its data), or -1.</param>
public sealed record RawScanNote(RawScanNoteKind Kind, string Detail, bool CouldHideText, int Count, long FirstOffset);

/// <summary>What a raw-bytes scan could not read; never empty-by-ignorance: every stream that was not fully searched is listed.</summary>
public sealed class RawScanNotes
{
    internal RawScanNotes(IReadOnlyList<RawScanNote> items, int streamsScanned, long inflatedBytes)
    {
        Items = items;
        StreamsScanned = streamsScanned;
        InflatedBytes = inflatedBytes;
    }

    /// <summary>Gets the notes, one per kind and detail.</summary>
    public IReadOnlyList<RawScanNote> Items { get; }

    /// <summary>Gets the number of streams that were unpacked and searched.</summary>
    public int StreamsScanned { get; }

    /// <summary>Gets the number of bytes the unpacked streams add up to.</summary>
    public long InflatedBytes { get; }

    /// <summary>Gets whether text could hide in something that was not read (see <see cref="RawScanNote.CouldHideText"/>).</summary>
    public bool CouldHideText => Items.Any(static n => n.CouldHideText);

    /// <summary>Describes every note in one English line.</summary>
    public IReadOnlyList<string> Lines() => [.. Items.Select(Describe)];

    internal static string Describe(RawScanNote note)
    {
        var where = note.Count > 1
            ? string.Create(CultureInfo.InvariantCulture, $"{note.Count} streams, the first at byte {note.FirstOffset}")
            : note.FirstOffset >= 0
                ? string.Create(CultureInfo.InvariantCulture, $"stream at byte {note.FirstOffset}")
                : "file";
        return note.Kind switch
        {
            RawScanNoteKind.UnsupportedFilter => $"Raw bytes: {where} use the filter {note.Detail}, which is not unpacked; their bytes were searched as they are.",
            RawScanNoteKind.ImageData => $"Raw bytes: {where} hold image data, which is searched as bytes only.",
            RawScanNoteKind.UnreadableStream => $"Raw bytes: {where} could not be unpacked completely ({note.Detail}); only what unpacked was searched.",
            RawScanNoteKind.StreamTooLarge => $"Raw bytes: {where} unpack to more than the limit of one stream; only the first part was searched.",
            RawScanNoteKind.TotalLimitReached => $"Raw bytes: {where} were not unpacked because the streams of the file already unpack to more than the limit of all streams.",
            RawScanNoteKind.Predictor => $"Raw bytes: {where} use a predictor, which is not undone; the unpacked bytes were searched as they are.",
            RawScanNoteKind.Encrypted => "Raw bytes: the file has an encryption entry, so its streams cannot be read.",
            RawScanNoteKind.TooManyMatches => $"Raw bytes: {note.Detail}",
            _ => note.Detail,
        };
    }
}

/// <summary>
/// What the cleanup of a redaction removes from a file, found by name in the raw bytes (also inside unpacked object streams): an
/// indication for the report, not an exact count of bookmarks or files.
/// </summary>
/// <param name="MetadataKeys">The <c>/Metadata</c> entries (the XMP of the document or of a page).</param>
/// <param name="JavaScriptActions">The <c>/JS</c> entries.</param>
/// <param name="EmbeddedFiles">The streams with <c>/Type /EmbeddedFile</c> (the attachments of the names tree and the files of paperclip annotations together).</param>
/// <param name="OutlineItems">The dictionaries with <c>/Title</c> and <c>/Parent</c> (bookmarks).</param>
/// <param name="PageLabelKeys">The <c>/PageLabels</c> entries.</param>
/// <param name="Complete">Whether every stream could be read; <see langword="false"/> means the counts may be too low.</param>
/// <param name="AttachmentAnnotations">The <c>/FileAttachment</c> names (paperclip annotations): their files come with the pages and are not cleaned, so only the rest of <paramref name="EmbeddedFiles"/> counts as cleaned.</param>
public sealed record CleanupInventory(int MetadataKeys, int JavaScriptActions, int EmbeddedFiles, int OutlineItems, int PageLabelKeys, bool Complete, int AttachmentAnnotations = 0);

using System.IO.Compression;

namespace Pagewing.Core.Redaction;

/// <summary>
/// Searches the bytes of a PDF file for texts, the way a leak would hide in them: whatever the page shows, the file may still
/// hold a text in an old revision, in metadata, in an attachment, in an object stream or in a font mapping. The scan reads the
/// file as it is (string syntax decoded, see <see cref="RawSyntaxScanner"/>) and unpacks the streams it can (Flate, LZW,
/// ASCIIHex, ASCII85, RunLength, in chains) in constant memory, with limits against decompression bombs. What it cannot read is
/// listed in <see cref="RawScanNotes"/>, never skipped silently. Texts are searched as given and in their composed and
/// decomposed Unicode form, in single-byte encodings (Latin-1, Windows-ANSI, PDFDocEncoding), UTF-8 and UTF-16 of both byte orders.
/// A place is counted once, however many views find it.
/// </summary>
public static class RawContentScanner
{
    /// <summary>Counts the occurrences of a text in the file.</summary>
    /// <param name="file">The file; it must be seekable and is read, never changed.</param>
    /// <param name="term">The text to find (not empty); case-sensitive.</param>
    /// <param name="limits">The limits against decompression bombs.</param>
    /// <param name="notes">What the scan could not (fully) read.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>The number of places where the text occurs, each counted once.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static int Count(Stream file, string term, RawScanLimits limits, out RawScanNotes notes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrEmpty(term);
        ArgumentNullException.ThrowIfNull(limits);
        var result = Scan(file, [term], limits, cancellationToken);
        notes = result.Notes;
        return (int)Math.Min(int.MaxValue, result.Counts[0]);
    }

    /// <summary>Counts what a cleanup removes from the file, by name (see <see cref="CleanupInventory"/>).</summary>
    /// <param name="file">The file; it must be seekable.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    public static CleanupInventory Inventory(Stream file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        var result = Scan(file, [], RawScanLimits.Default, cancellationToken);
        var names = result.Names;
        return new CleanupInventory(names.MetadataKeys, names.JavaScriptActions, names.EmbeddedFiles, names.OutlineItems, names.PageLabelKeys, !result.Notes.CouldHideText, names.AttachmentAnnotations);
    }

    /// <summary>Counts several texts in one pass.</summary>
    /// <param name="file">The file.</param>
    /// <param name="terms">The texts, not empty.</param>
    /// <param name="limits">The limits.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    internal static RawScanResult Scan(Stream file, IReadOnlyList<string> terms, RawScanLimits limits, CancellationToken cancellationToken)
    {
        if (!file.CanSeek || !file.CanRead)
        {
            throw new ArgumentException("The file must be a readable, seekable stream.", nameof(file));
        }

        return new RawScanEngine(file, terms, limits, cancellationToken).Run();
    }
}

/// <summary>The result of a scan: the count per text, what could not be read, and the names that were counted.</summary>
/// <param name="Counts">The places per text.</param>
/// <param name="Notes">What could not be read.</param>
/// <param name="Names">The counted names.</param>
internal sealed record RawScanResult(long[] Counts, RawScanNotes Notes, NameTally Names);

/// <summary>One scan of a file (state: the automaton, the notes, the limits used so far).</summary>
internal sealed class RawScanEngine
{
    private const int ChunkSize = 64 * 1024;
    private const int DecodeChunkSize = 8192;
    private const int BodyRingSize = 16 * 1024;
    private const int EndMarkerCarry = 8;

    private static readonly byte[] EndStream = "endstream"u8.ToArray();

    private readonly Stream file;
    private readonly RawScanLimits limits;
    private readonly CancellationToken cancellation;
    private readonly long[] counts;
    private readonly NoteBuilder notes = new();
    private readonly NameTally tally = new();
    private readonly IntervalStore fileStore;
    private readonly IntervalStore decodedStore;
    private readonly RawView? fileRaw;
    private readonly RawView? decodedRaw;
    private readonly RawSyntaxScanner body;
    private readonly RawSyntaxScanner payloadSyntax;
    private readonly RawSyntaxScanner decodedSyntax;
    private readonly BodyRing ring = new(BodyRingSize);
    private readonly byte[] chunk = new byte[ChunkSize];
    private readonly byte[] decodeChunk = new byte[DecodeChunkSize];
    private readonly byte[] endWindow = new byte[EndMarkerCarry + ChunkSize];
    private long inflatedTotal;
    private bool totalExhausted;

    public RawScanEngine(Stream file, IReadOnlyList<string> terms, RawScanLimits limits, CancellationToken cancellation)
    {
        this.file = file;
        this.limits = limits;
        this.cancellation = cancellation;
        counts = new long[terms.Count];
        fileStore = new IntervalStore(terms.Count);
        decodedStore = new IntervalStore(terms.Count);
        TermAutomaton? automaton = terms.Count == 0 ? null : TermAutomaton.Build(terms);
        if (automaton is not null)
        {
            fileRaw = new RawView(automaton, fileStore);
            decodedRaw = new RawView(automaton, decodedStore);
        }

        body = new RawSyntaxScanner(automaton, fileStore, tally, detectStreams: true);
        payloadSyntax = new RawSyntaxScanner(automaton, fileStore, tally, detectStreams: false);
        decodedSyntax = new RawSyntaxScanner(automaton, decodedStore, tally, detectStreams: false);
    }

    private enum LimitHit
    {
        None,
        Stream,
        Total,
    }

    public RawScanResult Run()
    {
        var length = file.Length;
        var position = 0L;
        while (position < length)
        {
            cancellation.ThrowIfCancellationRequested();
            file.Position = position;
            var read = file.Read(chunk, 0, chunk.Length);
            if (read <= 0)
            {
                break;
            }

            var span = chunk.AsSpan(0, read);
            var consumed = body.Feed(span, position);
            fileRaw?.Feed(span[..consumed], position);
            ring.Append(span[..consumed]);
            position += consumed;
            if (body.StreamStart)
            {
                position = HandleStream(position, length);
                body.Reset();
                fileRaw?.Reset();
            }
        }

        fileStore.FlushInto(counts);
        if (tally.EncryptSeen)
        {
            notes.Add(RawScanNoteKind.Encrypted, string.Empty, couldHideText: true, -1);
        }

        if (fileStore.Overflowed || decodedStore.Overflowed)
        {
            notes.Add(RawScanNoteKind.TooManyMatches, "a text occurs so often that matches were counted without their places, which may count one place twice.", couldHideText: false, -1);
        }

        return new RawScanResult(counts, notes.Build(), tally);
    }

    private static bool IsSupported(string filter) =>
        filter is "FlateDecode" or "ASCIIHexDecode" or "ASCII85Decode" or "RunLengthDecode" or "LZWDecode";

    private static bool IsImageCodec(string filter) =>
        filter is "DCTDecode" or "JPXDecode" or "CCITTFaxDecode" or "JBIG2Decode";

    private long HandleStream(long start, long length)
    {
        var info = StreamInfo.Parse(ring.Span);
        var end = FindEndStream(start, length);
        if (end > start)
        {
            ProcessPayload(info, start, end);
        }

        return end;
    }

    private long FindEndStream(long start, long length)
    {
        var window = endWindow;
        var have = 0;
        var position = start;
        while (position < length)
        {
            cancellation.ThrowIfCancellationRequested();
            file.Position = position;
            var read = file.Read(window, have, ChunkSize);
            if (read <= 0)
            {
                break;
            }

            var total = have + read;
            var found = window.AsSpan(0, total).IndexOf(EndStream);
            if (found >= 0)
            {
                return position - have + found;
            }

            var keep = Math.Min(EndMarkerCarry, total);
            window.AsSpan(total - keep, keep).CopyTo(window);
            have = keep;
            position += read;
        }

        return length;
    }

    private void ProcessPayload(StreamInfo info, long start, long end)
    {
        if (info.Problem is { } problem)
        {
            // The data may be packed in a way that is not known; it is searched as it is, and the stream is reported.
            notes.Add(RawScanNoteKind.UnreadableStream, problem, couldHideText: true, start);
        }

        if (info.IsImage)
        {
            notes.Add(RawScanNoteKind.ImageData, string.Empty, couldHideText: false, start);
            FeedRegion(start, end, syntax: null);
            return;
        }

        if (info.Filters.Count == 0)
        {
            FeedRegion(start, end, info.SkipSyntax ? null : payloadSyntax);
            return;
        }

        var unsupported = info.Filters.FirstOrDefault(static f => !IsSupported(f));
        if (unsupported is not null)
        {
            notes.Add(RawScanNoteKind.UnsupportedFilter, unsupported, couldHideText: !IsImageCodec(unsupported), start);
            FeedRegion(start, end, syntax: null);
            return;
        }

        if (totalExhausted)
        {
            notes.Add(RawScanNoteKind.TotalLimitReached, string.Empty, couldHideText: true, start);
            FeedRegion(start, end, syntax: null);
            return;
        }

        Decode(info, start, end);
    }

    /// <summary>Searches the bytes of the file between two offsets as they are.</summary>
    private void FeedRegion(long start, long end, RawSyntaxScanner? syntax)
    {
        fileRaw?.Reset();
        syntax?.Reset();
        var position = start;
        while (position < end)
        {
            cancellation.ThrowIfCancellationRequested();
            file.Position = position;
            var read = file.Read(chunk, 0, (int)Math.Min(chunk.Length, end - position));
            if (read <= 0)
            {
                break;
            }

            var span = chunk.AsSpan(0, read);
            fileRaw?.Feed(span, position);
            syntax?.Feed(span, position);
            position += read;
        }

        fileRaw?.Reset();
    }

    private Stream Build(StreamInfo info, long start, long end, List<EndProbeStream> probes)
    {
        Stream source = new RangeStream(file, start, end);
        foreach (var filter in info.Filters)
        {
            if (filter == "FlateDecode")
            {
                var probe = new EndProbeStream(source);
                probes.Add(probe);
                source = probe;
            }

            source = filter switch
            {
                "FlateDecode" => new ZLibStream(source, CompressionMode.Decompress),
                "ASCIIHexDecode" => new AsciiHexStream(source),
                "ASCII85Decode" => new Ascii85Stream(source),
                "RunLengthDecode" => new RunLengthStream(source),
                _ => new LzwStream(source, info.EarlyChange),
            };
        }

        return source;
    }

    private void Decode(StreamInfo info, long start, long end)
    {
        decodedRaw?.Reset();
        decodedSyntax.Reset();
        var streamTotal = 0L;
        var corrupt = false;
        var hit = LimitHit.None;
        var syntax = !info.SkipSyntax;
        var probes = new List<EndProbeStream>();
        try
        {
            using var source = Build(info, start, end, probes);
            hit = Pump(source, ref streamTotal, syntax);
        }
        catch (InvalidDataException)
        {
            corrupt = true;
            Salvage(info, start, end, ref streamTotal, syntax);
        }

        // A zlib stream that ends where its data ends, without its end marker, is cut off (the framework reports it as a normal end).
        if (hit == LimitHit.None && probes.Exists(static p => p.ReadPastEnd))
        {
            corrupt = true;
        }

        decodedStore.FlushInto(counts);
        notes.StreamsScanned++;
        notes.InflatedBytes += streamTotal;
        if (info.Predictor && !info.SkipSyntax)
        {
            notes.Add(RawScanNoteKind.Predictor, string.Empty, couldHideText: true, start);
        }

        switch (hit)
        {
            case LimitHit.Stream:
                notes.Add(RawScanNoteKind.StreamTooLarge, string.Empty, couldHideText: true, start);
                break;
            case LimitHit.Total:
                totalExhausted = true;
                notes.Add(RawScanNoteKind.TotalLimitReached, string.Empty, couldHideText: true, start);
                break;
            default:
                break;
        }

        if (corrupt)
        {
            notes.Add(RawScanNoteKind.UnreadableStream, "corrupt or truncated data", couldHideText: true, start);
            if (streamTotal == 0)
            {
                FeedRegion(start, end, syntax: null); // nothing unpacked: perhaps the data is not packed at all
            }
        }
    }

    private LimitHit Pump(Stream source, ref long streamTotal, bool syntax)
    {
        int read;
        while ((read = source.Read(decodeChunk, 0, decodeChunk.Length)) > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            var streamLeft = limits.MaxInflatedStream - streamTotal;
            var totalLeft = limits.MaxInflatedTotal - inflatedTotal;
            var take = (int)Math.Min(read, Math.Max(0, Math.Min(streamLeft, totalLeft)));
            if (take > 0)
            {
                FeedDecoded(decodeChunk.AsSpan(0, take), streamTotal, syntax);
            }

            streamTotal += take;
            inflatedTotal += take;
            if (take < read)
            {
                return streamLeft <= totalLeft ? LimitHit.Stream : LimitHit.Total;
            }
        }

        return LimitHit.None;
    }

    /// <summary>
    /// After a decoding error: the call that failed lost the bytes it had decoded, so the same data is decoded again, the part that
    /// worked is skipped, and the rest is read one byte at a time until the error (so a damaged tail does not hide what comes before it).
    /// </summary>
    private void Salvage(StreamInfo info, long start, long end, ref long streamTotal, bool syntax)
    {
        var rescued = 0;
        try
        {
            using var again = Build(info, start, end, []);
            var skipped = 0L;
            while (skipped < streamTotal)
            {
                var read = again.Read(decodeChunk, 0, (int)Math.Min(decodeChunk.Length, streamTotal - skipped));
                if (read <= 0)
                {
                    return;
                }

                skipped += read;
            }

            var one = new byte[1];
            while (rescued < DecodeChunkSize && again.Read(one, 0, 1) == 1)
            {
                decodeChunk[rescued++] = one[0];
            }
        }
        catch (InvalidDataException)
        {
            // The rest is damaged: what was read up to here is searched, the stream is reported by the caller.
        }

        FeedDecoded(decodeChunk.AsSpan(0, rescued), streamTotal, syntax);
        streamTotal += rescued;
        inflatedTotal += rescued;
    }

    private void FeedDecoded(ReadOnlySpan<byte> data, long offset, bool syntax)
    {
        decodedRaw?.Feed(data, offset);
        if (syntax)
        {
            decodedSyntax.Feed(data, offset);
        }
    }
}

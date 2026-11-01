using Pagewing.Core.Pdf;
using Pagewing.Core.Text;

namespace Pagewing.Core.Redaction;

/// <summary>
/// A text that redaction removed from a page (or left to remove everywhere), for the check of the result. The text must be real
/// Unicode text (from the terms of the plan or from the page text): <see cref="PageRedactionImpact.RemovedText"/> reports U+FFFD for
/// characters the engine could not read exactly, and a text with U+FFFD or NUL cannot be verified, so the check reports it as a
/// finding (<see cref="RedactionFindingKind.RemovedTextUnverifiable"/>) instead of skipping it quietly.
/// <para>
/// <see cref="OccurrencesBefore"/> is the number of occurrences of the text in the page text of the WHOLE ORIGINAL document, counted
/// with the matcher the check itself uses (<see cref="TextMatcher"/>, case-sensitive). Build it with
/// <see cref="FromOriginalAsync(PdfDocument, int, string, bool, CancellationToken)"/> and never type a number: the check expects at most
/// <c>OccurrencesBefore - (entries of the same text)</c> occurrences in the result, so an overstated number lets a leak pass and an
/// understated one fails a correct result (both are shown by tests).
/// </para>
/// </summary>
/// <param name="Page">The 0-based page the text was removed from (for the report; the counts are for the whole document).</param>
/// <param name="Text">The text as the page held it.</param>
/// <param name="Everywhere">Whether the text must be gone from the whole document.</param>
/// <param name="OccurrencesBefore">How often the text occurred in the text of the whole original document (with <see cref="Everywhere"/> it is not used).</param>
public sealed record RemovedText(int Page, string Text, bool Everywhere, int OccurrencesBefore)
{
    /// <summary>
    /// Builds an entry with <see cref="OccurrencesBefore"/> counted in the ORIGINAL document, document-wide, with the matcher and the
    /// text cut (<see cref="RedactionLimits.MaxTermLength"/>) the check uses.
    /// </summary>
    /// <param name="original">The original document (not the working copy and not the result).</param>
    /// <param name="page">The 0-based page the text was removed from.</param>
    /// <param name="text">The real text (not an impact string with U+FFFD).</param>
    /// <param name="everywhere">Whether the text must be gone from the whole document.</param>
    /// <param name="cancellationToken">Cancels between pages.</param>
    /// <exception cref="ArgumentException">The text is empty or only white space.</exception>
    public static async Task<RemovedText> FromOriginalAsync(PdfDocument original, int page, string text, bool everywhere, CancellationToken cancellationToken = default)
    {
        var built = await FromOriginalAsync(original, [new RemovedText(page, text, everywhere, 0)], cancellationToken).ConfigureAwait(false);
        return built[0];
    }

    /// <summary>Builds several entries in one pass over the original document; the <see cref="OccurrencesBefore"/> of the candidates is ignored.</summary>
    /// <param name="original">The original document.</param>
    /// <param name="candidates">The removed texts without a trustworthy count.</param>
    /// <param name="cancellationToken">Cancels between pages.</param>
    /// <exception cref="ArgumentException">A text is empty or only white space.</exception>
    public static async Task<IReadOnlyList<RemovedText>> FromOriginalAsync(PdfDocument original, IReadOnlyList<RemovedText> candidates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(candidates);
        var needles = new string[candidates.Count];
        for (var i = 0; i < needles.Length; i++)
        {
            ArgumentNullException.ThrowIfNull(candidates[i]);
            ArgumentNullException.ThrowIfNull(candidates[i].Text);
            needles[i] = TextMatcher.Normalize(RedactionVerifier.Truncate(candidates[i].Text), matchCase: true);
            if (needles[i].Length == 0)
            {
                throw new ArgumentException("A removed text must not be empty or only white space.", nameof(candidates));
            }
        }

        var counts = new int[needles.Length];
        for (var page = 0; page < original.PageCount; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await original.GetPageTextAsync(page, PdfPriority.Background, cancellationToken).ConfigureAwait(false);
            TextMatcher.CountNormalized(text.Chars, matchCase: true, needles, counts);
        }

        return [.. candidates.Select((c, i) => c with { OccurrencesBefore = counts[i] })];
    }
}

/// <summary>What the check expects of a result: the plan that was applied, the text that went, and the colour of the areas.</summary>
/// <param name="Plan">The marks that were applied.</param>
/// <param name="Removed">The texts that were removed, one entry per removed place.</param>
/// <param name="Fill">The colour the areas were filled with.</param>
public sealed record RedactionExpectation(RedactionPlan Plan, IReadOnlyList<RemovedText> Removed, RedactionFill Fill);

/// <summary>The kinds of finding of the check.</summary>
public enum RedactionFindingKind
{
    /// <summary>A removed text is still in the page text of the result, more often than the removal explains.</summary>
    TextStillPresent,

    /// <summary>A character of the result lies inside a marked area.</summary>
    CharInsideArea,

    /// <summary>A removed text is still in the bytes of the file (an old revision, metadata, an attachment, a pattern ...) more often than the page text shows it.</summary>
    RawBytesStillPresent,

    /// <summary>A pixel of an image under a marked area is not the colour of the area.</summary>
    ImagePixelNotOverwritten,

    /// <summary>Something could not be read, so the check cannot say whether text hides in it.</summary>
    UnreadableStream,

    /// <summary>A page still has a key that carries content the cleanup cannot remove (set by the apply step, not by <see cref="RedactionVerifier"/>).</summary>
    PageKeysStillPresent,

    /// <summary>A page looks different outside the marked areas (set by the apply step, not by <see cref="RedactionVerifier"/>).</summary>
    PageChangedOutsideAreas,

    /// <summary>A removed text cannot be verified because it is not real text (it contains U+FFFD or NUL); the caller must supply the real text.</summary>
    RemovedTextUnverifiable,

    /// <summary>Something the cleanup is meant to remove at the document level (a field of the information dictionary, bookmarks, the names tree, page labels, the XMP of the document ...) is still in the written file (set by the apply step; the detail names the item, never its content).</summary>
    CleanupItemStillPresent,
}

/// <summary>One thing the check found. The detail names the kind of the problem and the place, never the text itself.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Page">The 0-based page, or <see langword="null"/> when the finding concerns the file.</param>
/// <param name="Detail">A short English explanation.</param>
/// <param name="RelatedPages">
/// For a finding about a removed text that no single page shows (<see cref="RedactionFindingKind.RawBytesStillPresent"/>): the 0-based
/// pages the text was removed from or marked on, ascending; <see langword="null"/> otherwise. The page the content hides on is not
/// known, only where it was meant to go.
/// </param>
public sealed record RedactionFinding(RedactionFindingKind Kind, int? Page, string Detail, IReadOnlyList<int>? RelatedPages = null);

/// <summary>
/// What a line of <see cref="RedactionCheck.NotChecked"/> is about. The English line stays the text of the command line; the interface
/// builds its own sentence from the kind and the numbers (<see cref="RedactionNotChecked"/>), so a new line needs a new kind, and a test
/// fails for a kind the interface cannot say.
/// </summary>
public enum RedactionNotCheckedKind
{
    /// <summary>A line that has no kind of its own (the detail of a note); the interface shows the English text.</summary>
    Other,

    /// <summary>Removed texts that consist of white space only (<c>Count</c>).</summary>
    BlankTexts,

    /// <summary>Removed texts with a character the engine could not read exactly (<c>Count</c>).</summary>
    UnreadableChars,

    /// <summary>Removed texts longer than the limit (<c>Count</c>).</summary>
    TruncatedTexts,

    /// <summary>Distinct removed texts beyond the limit (<c>Count</c>, the limit in <c>Second</c>).</summary>
    FurtherTexts,

    /// <summary>Removed texts too short for the search in the file bytes (<c>Count</c>, the shortest length in <c>Second</c>).</summary>
    RawShortTexts,

    /// <summary>No text was removed, so the file bytes were not searched.</summary>
    RawNothingRemoved,

    /// <summary>Removed texts that still occur in the page text, so a leaked copy cannot be told from them (<c>Count</c> texts, <c>Second</c> occurrences).</summary>
    RawStillOccur,

    /// <summary>Streams with a filter that is not unpacked (<c>Count</c>, <c>Offset</c> of the first, the filter in <c>Detail</c>).</summary>
    RawFilter,

    /// <summary>Streams with image data, searched as bytes only (<c>Count</c>, <c>Offset</c>).</summary>
    RawImageStreams,

    /// <summary>Streams that could not be unpacked completely (<c>Count</c>, <c>Offset</c>, the reason in <c>Detail</c>).</summary>
    RawUnreadableStream,

    /// <summary>Streams that unpack to more than the limit of one stream (<c>Count</c>, <c>Offset</c>).</summary>
    RawStreamTooLarge,

    /// <summary>Streams not unpacked because the streams of the file reached the total limit (<c>Count</c>, <c>Offset</c>).</summary>
    RawTotalLimit,

    /// <summary>Streams with a predictor that is not undone (<c>Count</c>, <c>Offset</c>).</summary>
    RawPredictor,

    /// <summary>The file has an encryption entry, so its streams cannot be read.</summary>
    RawEncrypted,

    /// <summary>Fonts with their own code mapping: the text is not readable in the streams.</summary>
    RawFontMapping,

    /// <summary>The encodings that are searched; others are not found.</summary>
    RawEncodings,

    /// <summary>A text split across text objects, XML entities and unplanned case variants are not found.</summary>
    RawSplitText,

    /// <summary>Image data is searched as bytes only and attachments are not looked into.</summary>
    RawImageAttachments,

    /// <summary>Marked areas beyond the limit that were not read back (<c>Count</c>, the limit in <c>Second</c>).</summary>
    PixelsBeyond,

    /// <summary>Only images that touch a marked area are read back.</summary>
    PixelsOnlyImages,

    /// <summary>Another program may read the file differently.</summary>
    OtherViewer,

    /// <summary>The apply step compares only the redacted pages with the original, outside the marked areas (forms: annotations left out).</summary>
    ApplyRender,

    /// <summary>The apply step looks for page keys in the page dictionaries only.</summary>
    ApplyPageKeys,

    /// <summary>XMP and JavaScript were removed from the document level only; the entries inside page-level objects are not read back.</summary>
    ApplyXmp,
}

/// <summary>One line of what the check did not check, with the data the interface needs to say it in its own language.</summary>
/// <param name="Kind">What it is about.</param>
/// <param name="Text">The English line (the same as the entry of <see cref="RedactionCheck.NotChecked"/>).</param>
/// <param name="Count">The main number (see the kind), or 0.</param>
/// <param name="Second">The second number (see the kind), or 0.</param>
/// <param name="Detail">A technical detail the engine gives in English only (a filter name, the reason a stream could not be unpacked), or empty.</param>
/// <param name="Offset">The file offset of the first stream concerned, or -1.</param>
public sealed record RedactionNotChecked(RedactionNotCheckedKind Kind, string Text, int Count = 0, int Second = 0, string Detail = "", long Offset = -1);

/// <summary>The lines of what was not checked, as English text and as data, always added together (a line without a kind cannot be added).</summary>
internal sealed class NotCheckedLog
{
    private readonly List<string> lines = [];
    private readonly List<RedactionNotChecked> items = [];

    public IReadOnlyList<string> Lines => lines;

    public IReadOnlyList<RedactionNotChecked> Items => items;

    public void Add(RedactionNotCheckedKind kind, string text, int count = 0, int second = 0, string detail = "", long offset = -1)
    {
        lines.Add(text);
        items.Add(new RedactionNotChecked(kind, text, count, second, detail, offset));
    }
}

/// <summary>The result of the check: what it found and, always, what it did not check.</summary>
/// <param name="Findings">The findings; none means the check passed.</param>
/// <param name="NotChecked">What could not be checked, one line each; never empty by omission: the report never promises more than the check does.</param>
/// <param name="NotCheckedItems">The same lines as data, in the same order (<see cref="RedactionNotChecked"/>), or <see langword="null"/> for a check made of text alone.</param>
public sealed record RedactionCheck(IReadOnlyList<RedactionFinding> Findings, IReadOnlyList<string> NotChecked, IReadOnlyList<RedactionNotChecked>? NotCheckedItems = null)
{
    /// <summary>Gets whether the check found nothing.</summary>
    public bool IsClean => Findings.Count == 0;

    /// <summary>Gets the lines of <see cref="NotChecked"/> as data, in the same order; empty for a check made of text alone.</summary>
    public IReadOnlyList<RedactionNotChecked> Items => NotCheckedItems ?? [];
}

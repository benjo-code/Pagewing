using System.Text;
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
/// <param name="RawForm">
/// For a pattern hit that is written in groups ("DE89 3704 ..."): the text without its separators (<see cref="RawFormOf"/>), which the
/// raw-bytes check looks for as well, because the spaces between groups are not in the content stream when they come from the spacing
/// of the glyphs. <see langword="null"/> for everything else.
/// </param>
public sealed record RemovedText(int Page, string Text, bool Everywhere, int OccurrencesBefore, string? RawForm = null)
{
    /// <summary>
    /// The form of a text without separators for the raw-bytes check: spaces (also no-break, narrow no-break and thin spaces), hyphens,
    /// slashes, dots and round brackets removed. A form that is not shorter than the text itself carries nothing new and a form under
    /// <see cref="RedactionLimits.MinRawCheckLength"/> characters would match by chance; both give <see langword="null"/>.
    /// </summary>
    /// <param name="text">The text of the hit.</param>
    /// <returns>The form without separators, or <see langword="null"/>.</returns>
    public static string? RawFormOf(string text)
    {
        var stripped = WithoutSeparators(text);
        return stripped.Length < text.Length && stripped.Length >= RedactionLimits.MinRawCheckLength ? stripped : null;
    }

    /// <summary>
    /// The text without any separator (the set of <see cref="RawFormOf"/>: white space of every kind, hyphens, slashes, dots, brackets, soft
    /// hyphen). Always a string, however short; <see cref="RedactionOverlayRule"/> compares overlay text and terms in this form as well.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The text with every separator removed.</returns>
    internal static string WithoutSeparators(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!IsSeparator(c))
            {
                builder.Append(c);
            }
        }

        return builder.Length == text.Length ? text : builder.ToString();
    }

    private static bool IsSeparator(char c) =>
        char.IsWhiteSpace(c) || c is '-' or '‐' or '‑' or '/' or '.' or '(' or ')' or '[' or ']' or '­';

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
    public static Task<IReadOnlyList<RemovedText>> FromOriginalAsync(PdfDocument original, IReadOnlyList<RemovedText> candidates, CancellationToken cancellationToken = default) =>
        FromOriginalAsync(original, candidates, excludedPages: null, cancellationToken);

    /// <summary>
    /// As <see cref="FromOriginalAsync(PdfDocument, IReadOnlyList{RemovedText}, CancellationToken)"/>, without counting the text of
    /// <paramref name="excludedPages"/>: the pages that become images have no text in the result, so the occurrences on them are gone
    /// anyway and must not count as a place that stays (the rule of the check is "at most the occurrences before, minus the removed places").
    /// </summary>
    /// <param name="original">The original document.</param>
    /// <param name="candidates">The removed texts without a trustworthy count.</param>
    /// <param name="excludedPages">The 0-based pages whose text is not counted, or <see langword="null"/> for none.</param>
    /// <param name="cancellationToken">Cancels between pages.</param>
    /// <exception cref="ArgumentException">A text is empty or only white space.</exception>
    public static async Task<IReadOnlyList<RemovedText>> FromOriginalAsync(PdfDocument original, IReadOnlyList<RemovedText> candidates, IReadOnlySet<int>? excludedPages, CancellationToken cancellationToken = default)
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
            if (excludedPages is not null && excludedPages.Contains(page))
            {
                continue;
            }

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
/// <param name="RasterPages">The 0-based pages that were turned into images (the pages of <see cref="RedactionPlan.RasterPages"/>), ascending; <see langword="null"/> for none. Their text is gone, so the places of marks on them do not count in the occurrence rule.</param>
/// <param name="ImageAreas">
/// For a plan that pixelates or blurs (<see cref="RedactionPlan.ImageTreatment"/>): what the images of the ORIGINAL held under each area, as
/// block means (<see cref="ExpectedImageArea"/>), so that the check accepts the block result next to the fill colour. <see langword="null"/>
/// or empty: only the fill colour is accepted, which is the strict answer for a plan that treats image areas solid.
/// </param>
public sealed record RedactionExpectation(
    RedactionPlan Plan,
    IReadOnlyList<RemovedText> Removed,
    RedactionFill Fill,
    IReadOnlyList<int>? RasterPages = null,
    IReadOnlyList<ExpectedImageArea>? ImageAreas = null);

/// <summary>
/// What one image of the ORIGINAL held under one area of the plan, reduced to what pixelating or blurring keeps of it: the block means.
/// The check does not need (and does not keep) the original pixels; it works the expected pixels out of these means with
/// <see cref="ImageBlocks.ExpectedFromMeans"/>, and the result of the written file must equal them (blur: within
/// <see cref="RedactionVerifier.BlurTolerance"/> per channel) or the fill colour.
/// </summary>
/// <param name="Page">The 0-based page.</param>
/// <param name="Area">The area of the mark, as the plan has it.</param>
/// <param name="Width">The width in pixels of the part of the image under the area (the pixel rectangle that rounds the area outwards).</param>
/// <param name="Height">The height in pixels of that part.</param>
/// <param name="BlockMeans">The means of its blocks (blue, green, red, row by row, <see cref="ImageBlocks.Plan"/> of <paramref name="Width"/> x <paramref name="Height"/> for the strength of the plan).</param>
public sealed record ExpectedImageArea(int Page, PageRect Area, int Width, int Height, byte[] BlockMeans);

/// <summary>The kinds of finding of the check.</summary>
public enum RedactionFindingKind
{
    /// <summary>A removed text is still in the page text of the result, more often than the removal explains.</summary>
    TextStillPresent,

    /// <summary>A character of the result lies inside a marked area.</summary>
    CharInsideArea,

    /// <summary>A removed text is still in the bytes of the file (an old revision, metadata, an attachment, a pattern ...) more often than the page text shows it.</summary>
    RawBytesStillPresent,

    /// <summary>A pixel of an image under a marked area is not the colour of the area (and where pixelating or blurring was chosen, the area is not a treated one either).</summary>
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

    /// <summary>A page that was turned into an image is not just one image (more or no image, text, paths, other objects, annotations, links or form fields), or could not be counted; the detail names the counts, never content.</summary>
    RasterPageInvalid,

    /// <summary>
    /// An image area of a plan that pixelates or blurs is neither the fill colour nor exactly the block result worked out from the original
    /// (the original pixels, other blocks, the wrong strength or treatment, a partly treated area); the detail names counts and places,
    /// never pixel values.
    /// </summary>
    ImageTreatmentWrong,
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

    /// <summary>The apply step compares only the pages redacted in place with the original, outside the marked areas (forms: annotations left out); pages turned into images are not compared (<c>Count</c> of them, when there are any).</summary>
    ApplyRender,

    /// <summary>The apply step looks for page keys in the page dictionaries only.</summary>
    ApplyPageKeys,

    /// <summary>XMP and JavaScript were removed from the document level only; the entries inside page-level objects are not read back.</summary>
    ApplyXmp,

    /// <summary>Pages turned into images (<c>Count</c> pages): a visible occurrence of a removed text outside the marked areas stays in the image and cannot be found without OCR.</summary>
    RasterOutsideAreas,

    /// <summary>Image areas that were pixelated or blurred (<c>Count</c> pixelated or blurred, <c>Second</c> filled solid): the check compares them with the block means, but whether text inside the image stays legible is not checked, and Pagewing does not recognise text in images.</summary>
    ImageTextLegible,

    /// <summary>Overlay text was part of the plan: it is drawn as shapes on the areas and is not read back from the written file.</summary>
    OverlayTextNotRead,
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

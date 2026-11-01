using System.Runtime.CompilerServices;
using Pagewing.Core.Annotations;
using Pagewing.Core.Forms;
using Pagewing.Core.Pdf;
using Pagewing.Core.Text;

namespace Pagewing.Core.Redaction;

/// <summary>Where a <see cref="RedactionHit"/> was found.</summary>
public enum RedactionHitSource
{
    /// <summary>The text of the page, invisible text included.</summary>
    PageText,

    /// <summary>The text (comment, text of a text box) of an annotation; the area is the whole annotation.</summary>
    Annotation,

    /// <summary>The value of a form field (text, chosen option, export value of a radio button); the area is the widget.</summary>
    FormField,
}

/// <summary>
/// A place where "search and redact" found the term. A hit that cannot become a mark says so with <see cref="Markable"/> (it is
/// never an exception, so a list can show it and say why nothing happens).
/// </summary>
/// <param name="Source">Where it was found.</param>
/// <param name="Page">The 0-based page.</param>
/// <param name="Areas">The rectangles in points on that page, origin top left: one per line for page text, the annotation or the widget otherwise. Rectangles without an area are not in the list.</param>
/// <param name="Text">The matched text exactly as the page has it (letter case, ligatures and decomposed letters as they are there; a line break inside is one space, a hyphen at a line end that the search drops is left out). This is what the check compares, not the query.</param>
/// <param name="Invisible">Whether the matched page text shows nothing (render mode "invisible", the text layer of an OCR scan). Always <see langword="false"/> for the other sources. Text that is covered by something is not detected.</param>
public sealed record RedactionHit(RedactionHitSource Source, int Page, IReadOnlyList<PageRect> Areas, string Text, bool Invisible)
{
    /// <summary>
    /// Gets whether the hit can become a mark: it has at least one rectangle with an area (and at most
    /// <see cref="RedactionLimits.MaxAreasPerMark"/>), and its text is not white space only and not longer than
    /// <see cref="RedactionLimits.MaxTermLength"/>.
    /// </summary>
    public bool Markable { get; init; } = true;

    /// <summary>Gets the text around the match, for a list of hits.</summary>
    public SearchSnippet Snippet { get; init; } = new(string.Empty, string.Empty, string.Empty);

    /// <summary>Gets what holds the match besides the page: the PDF subtype of an annotation, the name of a form field; <see langword="null"/> for page text.</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// "Search and redact" (milestone 14): finds a term in everything that can hold it and turns hits into marks. The comparison is that
/// of the search panel (<see cref="TextMatcher"/>: Unicode normalization form KC, letter case unless asked, white space and line
/// breaks as one space, hyphenated line ends joined), and the query is text, never a pattern.
/// </summary>
public static class RedactionSearch
{
    /// <summary>The most hits one search yields (equal to the most marks a plan holds); the search ends after the hit that reaches it.</summary>
    public const int MaxHits = RedactionLimits.MaxMarks;

    /// <summary>The most UTF-16 units of the text of one annotation or form field that are searched; the rest is not looked at and the page counts as incomplete.</summary>
    public const int MaxSourceTextLength = 2_000_000;

    /// <summary>
    /// Finds the term on all pages, page by page, in the page text (invisible text too), the texts of the annotations and the values of
    /// the form fields, each as its own source. A page that cannot be read is skipped (see the overload that reports such pages).
    /// </summary>
    /// <param name="document">The document to search.</param>
    /// <param name="term">The text to find; not empty, not white space only, at most <see cref="RedactionLimits.MaxTermLength"/> units.</param>
    /// <param name="options">Letter case and whole words.</param>
    /// <param name="cancellationToken">Stops the search before the next page.</param>
    /// <returns>The hits in page order; in page text order within a page, then annotations, then form fields.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> or <paramref name="term"/> is <see langword="null"/>.</exception>
    /// <exception cref="RedactionException"><paramref name="term"/> is empty or white space only (<see cref="RedactionError.EmptyTerm"/>) or too long (<see cref="RedactionError.LimitExceeded"/>); thrown by the call, not by the enumeration.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static IAsyncEnumerable<RedactionHit> FindAsync(PdfDocument document, string term, SearchOptions options, CancellationToken cancellationToken) =>
        FindAsync(document, term, options, null, null, cancellationToken);

    /// <summary>
    /// Like <see cref="FindAsync(PdfDocument, string, SearchOptions, CancellationToken)"/>, with the page texts taken from
    /// <paramref name="texts"/> (the cache of the session, 32 pages) and a list of the pages where something could not be searched.
    /// </summary>
    /// <param name="document">The document to search.</param>
    /// <param name="term">The text to find.</param>
    /// <param name="options">Letter case and whole words.</param>
    /// <param name="texts">The cache of page texts, which must belong to <paramref name="document"/>; <see langword="null"/> reads every page from the document.</param>
    /// <param name="incompletePages">Receives the 0-based pages where the text, the annotations or the fields could not be read, or a text was too long (<see cref="MaxSourceTextLength"/>); such a page was searched only as far as it could be. May be <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops the search before the next page.</param>
    /// <returns>The hits, at most <see cref="MaxHits"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> or <paramref name="term"/> is <see langword="null"/>.</exception>
    /// <exception cref="RedactionException">The term is empty, white space only or too long; thrown by the call, not by the enumeration.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static IAsyncEnumerable<RedactionHit> FindAsync(
        PdfDocument document,
        string term,
        SearchOptions options,
        PageTextSource? texts,
        ICollection<int>? incompletePages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(term);
        if (string.IsNullOrWhiteSpace(term))
        {
            throw new RedactionException(RedactionError.EmptyTerm, "The term is empty or only white space.");
        }

        if (term.Length > RedactionLimits.MaxTermLength)
        {
            throw new RedactionException(RedactionError.LimitExceeded, "The term is longer than allowed.");
        }

        return FindCoreAsync(document, term, options, texts, incompletePages, cancellationToken);
    }

    /// <summary>
    /// Makes a mark of a hit. <see cref="RedactionMark.Term"/> is the matched text of the hit (<see cref="RedactionHit.Text"/>), exactly as
    /// the page has it, and never <paramref name="term"/>: the check compares letter for letter, so a query "müller" that matched
    /// "MÜLLER" must remove and verify "MÜLLER". With <paramref name="everywhere"/> every hit has its own mark with its own text, so each
    /// spelling the search found is checked to be gone from the whole document. <paramref name="term"/> only has to be a query
    /// (not empty); it is not stored.
    /// </summary>
    /// <param name="hit">The hit.</param>
    /// <param name="term">The query the hit came from.</param>
    /// <param name="everywhere">Whether the text must be gone from the whole document.</param>
    /// <returns>A mark of the kind <see cref="RedactionKind.Hit"/> with a new id.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="RedactionException">The hit is not <see cref="RedactionHit.Markable"/> (<see cref="RedactionError.InvalidArea"/>), or the query is empty.</exception>
    public static RedactionMark ToMark(RedactionHit hit, string term, bool everywhere)
    {
        ArgumentNullException.ThrowIfNull(hit);
        ArgumentNullException.ThrowIfNull(term);
        if (string.IsNullOrWhiteSpace(term))
        {
            throw new RedactionException(RedactionError.EmptyTerm, "The term is empty or only white space.");
        }

        if (!hit.Markable)
        {
            throw new RedactionException(RedactionError.InvalidArea, "The hit has no area that can be marked.");
        }

        return new RedactionMark(Guid.NewGuid(), RedactionKind.Hit, hit.Page, [.. hit.Areas], hit.Text, everywhere);
    }

    /// <summary>
    /// Makes marks of all hits that can become one, for <see cref="RedactionPlan.WithRange"/>; the others are counted, not made.
    /// Enumerates the hits once.
    /// </summary>
    /// <param name="hits">The hits.</param>
    /// <param name="term">The query the hits came from.</param>
    /// <param name="everywhere">Whether each text must be gone from the whole document.</param>
    /// <param name="notMarkable">How many hits were left out because they are not <see cref="RedactionHit.Markable"/>.</param>
    /// <returns>The marks in the order of the hits.</returns>
    public static IReadOnlyList<RedactionMark> ToMarks(IEnumerable<RedactionHit> hits, string term, bool everywhere, out int notMarkable)
    {
        ArgumentNullException.ThrowIfNull(hits);
        var marks = new List<RedactionMark>();
        notMarkable = 0;
        foreach (var hit in hits)
        {
            if (hit.Markable)
            {
                marks.Add(ToMark(hit, term, everywhere));
            }
            else
            {
                notMarkable++;
            }
        }

        return marks;
    }

    private static async IAsyncEnumerable<RedactionHit> FindCoreAsync(
        PdfDocument document,
        string term,
        SearchOptions options,
        PageTextSource? texts,
        ICollection<int>? incompletePages,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var total = 0;
        var pageCount = document.PageCount;
        for (var page = 0; page < pageCount; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var incomplete = false;

            PageText? pageText = null;
            try
            {
                pageText = texts is null
                    ? await document.GetPageTextAsync(page, PdfPriority.Background, cancellationToken).ConfigureAwait(false)
                    : await texts.GetAsync(page, PdfPriority.Background, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                incomplete = true;
            }

            if (pageText is not null)
            {
                var found = TextMatcher.Find(pageText.Chars, term, options);
                if (found.Count > 0)
                {
                    var take = Math.Min(found.Count, MaxHits - total);
                    IReadOnlyList<TextMatch> matches = found.Count == take ? found : [.. found.Take(take)];
                    foreach (var hit in await PageTextHitsAsync(document, page, pageText, matches, cancellationToken).ConfigureAwait(false))
                    {
                        total++;
                        yield return hit;
                    }

                    if (total >= MaxHits)
                    {
                        yield break;
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<PdfAnnotation> annotations = [];
            try
            {
                annotations = await document.GetAnnotationsAsync(page, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                incomplete = true;
            }

            foreach (var annotation in annotations)
            {
                if (string.IsNullOrEmpty(annotation.Contents))
                {
                    continue;
                }

                foreach (var hit in TextHits(RedactionHitSource.Annotation, page, annotation.Contents, annotation.Bounds, annotation.Subtype, term, options, ref incomplete))
                {
                    total++;
                    yield return hit;
                    if (total >= MaxHits)
                    {
                        yield break;
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<FormField> fields = [];
            try
            {
                fields = await document.GetFormFieldsAsync(page, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                incomplete = true;
            }

            foreach (var field in fields)
            {
                foreach (var value in ValuesOf(field))
                {
                    foreach (var hit in TextHits(RedactionHitSource.FormField, page, value, field.Bounds, field.Key.FieldName, term, options, ref incomplete))
                    {
                        total++;
                        yield return hit;
                        if (total >= MaxHits)
                        {
                            yield break;
                        }
                    }
                }
            }

            if (incomplete)
            {
                incompletePages?.Add(page);
            }
        }
    }

    private static async Task<List<RedactionHit>> PageTextHitsAsync(
        PdfDocument document,
        int page,
        PageText pageText,
        IReadOnlyList<TextMatch> matches,
        CancellationToken cancellationToken)
    {
        var chars = pageText.Chars;
        bool[] invisible;
        try
        {
            invisible = await document.GetInvisibleTextAsync(page, [.. matches.Select(static m => (m.FirstChar, m.LastChar))], cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            // Only the flag is lost; the hits are real.
            invisible = new bool[matches.Count];
        }

        var hits = new List<RedactionHit>(matches.Count);
        for (var i = 0; i < matches.Count; i++)
        {
            var (first, last) = matches[i];
            var snippet = TextMatcher.SnippetOf(chars, first, last);
            hits.Add(Make(RedactionHitSource.PageText, page, AreasOf(chars, first, last), snippet, null, invisible[i]));
        }

        return hits;
    }

    private static List<RedactionHit> TextHits(
        RedactionHitSource source,
        int page,
        string text,
        PageRect area,
        string detail,
        string term,
        SearchOptions options,
        ref bool incomplete)
    {
        if (text.Length > MaxSourceTextLength)
        {
            text = text[..MaxSourceTextLength];
            incomplete = true;
        }

        var chars = new List<TextChar>(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            chars.Add(new TextChar(rune.Value, default, TextCharTraits.None, 0));
        }

        var hits = new List<RedactionHit>();
        foreach (var (first, last) in TextMatcher.Find(chars, term, options))
        {
            hits.Add(Make(source, page, IsUsable(area) ? [area] : [], TextMatcher.SnippetOf(chars, first, last), detail, false));
        }

        return hits;
    }

    internal static RedactionHit Make(RedactionHitSource source, int page, List<PageRect> areas, SearchSnippet snippet, string? detail, bool invisible)
    {
        var text = snippet.Match;
        var markable = areas.Count is > 0 and <= RedactionLimits.MaxAreasPerMark
            && !string.IsNullOrWhiteSpace(text)
            && text.Length <= RedactionLimits.MaxTermLength;
        return new RedactionHit(source, page, areas, text, invisible) { Markable = markable, Snippet = snippet, Detail = detail };
    }

    /// <summary>
    /// The rectangles of the characters first to last, one per line, like <see cref="PageText.RectsOf"/>, but a character whose box has no
    /// area or is not finite (a zero-width joiner, a combining mark, a box the engine could not read) is left out: it would not
    /// be a valid rectangle, and in a union it would stretch the rectangle to the corner of the page. A line with no usable box gives
    /// no rectangle.
    /// </summary>
    internal static List<PageRect> AreasOf(IReadOnlyList<TextChar> chars, int first, int last)
    {
        var rects = new List<PageRect>();
        var end = last + 1;
        var i = first;
        while (i < end)
        {
            var segmentEnd = Math.Min(TextGeometry.SegmentEnd(chars, i), end);
            var from = -1;
            var to = -1;
            for (var k = i; k < segmentEnd; k++)
            {
                if (!chars[k].IsGenerated && IsUsable(chars[k].Box))
                {
                    from = from < 0 ? k : from;
                    to = k;
                }
            }

            if (from >= 0)
            {
                var union = chars[from].Box;
                for (var k = from + 1; k <= to; k++)
                {
                    if (IsUsable(chars[k].Box))
                    {
                        union = union.Union(chars[k].Box);
                    }
                }

                rects.Add(union);
            }

            i = segmentEnd;
            if (i < end && chars[i].IsLineBreak)
            {
                i++;
            }
        }

        return rects;
    }

    private static bool IsUsable(PageRect box) =>
        double.IsFinite(box.Left) && double.IsFinite(box.Top) && double.IsFinite(box.Right) && double.IsFinite(box.Bottom)
        && box.Right > box.Left && box.Bottom > box.Top;

    /// <summary>The strings of a field that a reader of the file can see: the text value, the labels of the chosen options.</summary>
    private static IEnumerable<string> ValuesOf(FormField field)
    {
        if (field.Kind == FormFieldKind.RadioButton)
        {
            // Every button of the group reports the value of the group; only the chosen one carries it.
            if (field.State.Text is { Length: > 0 } chosen && (field.ExportValue is null || string.Equals(field.ExportValue, chosen, StringComparison.Ordinal)))
            {
                yield return chosen;
            }

            yield break;
        }

        if (!string.IsNullOrEmpty(field.State.Text))
        {
            yield return field.State.Text;
        }

        foreach (var index in field.State.SelectedOptions)
        {
            if (index >= 0 && index < field.Options.Count && !string.IsNullOrEmpty(field.Options[index].Label))
            {
                yield return field.Options[index].Label;
            }
        }
    }
}

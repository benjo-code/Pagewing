using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
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

    /// <summary>Gets why <see cref="Markable"/> is <see langword="false"/> (English, for the list of hits); <see langword="null"/> for a hit that can be marked.</summary>
    public string? NotMarkableReason { get; init; }

    /// <summary>Gets the kind of pattern that found the hit (<see cref="RedactionPatternSearch"/>); <see langword="null"/> for the hit of a term.</summary>
    public RedactionPatternKind? Pattern { get; init; }

    /// <summary>Gets how sure the pattern scanner is about the hit; <see langword="null"/> exactly when <see cref="Pattern"/> is.</summary>
    public PatternCertainty? Certainty { get; init; }
}

/// <summary>
/// What a search could not do, filled in while the hits are enumerated and complete when the enumeration has ended. A search that
/// ends without a hit and with <see cref="IsComplete"/> <see langword="false"/> has not shown that the term is absent.
/// </summary>
public sealed class RedactionSearchReport
{
    private readonly SortedSet<int> pages = [];

    /// <summary>
    /// Gets the 0-based pages where something was not searched: the text, the annotations or the form fields could not be read, an
    /// annotation or field was left out by a limit or because it is damaged, or a text was too long
    /// (<see cref="RedactionSearch.MaxSourceTextLength"/>). Such a page may hold the term anyway. For the limit on annotations
    /// that the whole document shares (links and form widgets count towards it) every later page that has annotations is listed.
    /// </summary>
    public IReadOnlyCollection<int> IncompletePages => pages;

    /// <summary>Gets whether the search stopped at <see cref="RedactionSearch.MaxHits"/> hits; more may exist.</summary>
    public bool LimitReached { get; internal set; }

    /// <summary>Gets whether every page was searched completely and the limit was not reached. Does not cover <see cref="RedactionSearch.NotSearched"/>.</summary>
    public bool IsComplete => pages.Count == 0 && !LimitReached;

    internal void MarkIncomplete(int page) => pages.Add(page);
}

/// <summary>
/// "Search and redact" (milestone 14): finds a term in everything that can hold it and turns hits into marks. The comparison is that
/// of the search panel (<see cref="TextMatcher"/>: Unicode normalization form KC, letter case unless asked, white space and line
/// breaks as one space, hyphenated line ends joined), and the query is text, never a pattern.
/// </summary>
public static class RedactionSearch
{
    /// <summary>
    /// The most hits one search yields (equal to the most marks a plan holds); the search ends after the hit that reaches it.
    /// A search that yields exactly this many hits has reached the limit: more may exist
    /// (<see cref="RedactionSearchReport.LimitReached"/>).
    /// </summary>
    public const int MaxHits = RedactionLimits.MaxMarks;

    /// <summary>The most UTF-16 units of the text of one annotation or form field that are searched; the rest is not looked at and the page counts as incomplete.</summary>
    public const int MaxSourceTextLength = 2_000_000;

    /// <summary>
    /// What the search never looks at, as stable English lines for a card or the output of a command: a term that occurs only
    /// there is not found, and the check of the result does not know of it unless a mark names the text.
    /// </summary>
    public static IReadOnlyList<string> NotSearched { get; } =
    [
        "Replies to annotations (/IRT) and popup notes are not searched.",
        "The author (/T) and the subject (/Subj) of annotations are not searched.",
        "The tooltips (/TU) and the names of form fields are not searched.",
        "The rich text (/RV) of annotations and the default values (/DV) of form fields are not searched.",
        "Text that exists only in the appearance (/AP) of an annotation is not searched.",
        "Invisible text is flagged only for the render mode 3 (invisible); text that is covered by something else is found but not flagged.",
        "The values of form fields in a file without a form (widgets without /AcroForm) are not searched.",
        "Patterns (e-mail address, IBAN, telephone number, card number) find only what the text shows and what their built-in rules know: one split across a line break or a page boundary, digits interrupted by other characters than the usual separators, numbers inside images, IBANs of countries outside the built-in table, telephone numbers that do not start with +, 0 or (, digit chains of more than 64 groups, e-mail addresses whose local part has unusual characters (& = * !), and card numbers grouped in other layouts are not found.",
    ];

    /// <summary>
    /// Finds the term on all pages, page by page, in the page text (invisible text too), the texts of the annotations and the values of
    /// the form fields, each as its own source. This overload cannot say what it did not search: a page that cannot be read, an
    /// annotation or field that is left out by a limit, and the end at <see cref="MaxHits"/> are silent. Use the overload with a
    /// <see cref="RedactionSearchReport"/> wherever the result is shown or acted on.
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
    /// The search to use: like <see cref="FindAsync(PdfDocument, string, SearchOptions, CancellationToken)"/>, with the page texts
    /// taken from <paramref name="texts"/> and a report of what could not be searched. Read the report when the enumeration has
    /// ended; show <see cref="RedactionSearchReport.IncompletePages"/> and <see cref="NotSearched"/> with the result.
    /// </summary>
    /// <param name="document">The document to search.</param>
    /// <param name="term">The text to find.</param>
    /// <param name="options">Letter case and whole words.</param>
    /// <param name="report">Receives the pages that were not searched completely and whether the limit was reached; may be <see langword="null"/>.</param>
    /// <param name="texts">The cache of page texts (32 pages), which must belong to <paramref name="document"/>; <see langword="null"/> reads every page from the document.</param>
    /// <param name="cancellationToken">Stops the search before the next page.</param>
    /// <returns>The hits, at most <see cref="MaxHits"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> or <paramref name="term"/> is <see langword="null"/>.</exception>
    /// <exception cref="RedactionException">The term is empty, white space only or too long; thrown by the call, not by the enumeration.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static IAsyncEnumerable<RedactionHit> FindAsync(
        PdfDocument document,
        string term,
        SearchOptions options,
        RedactionSearchReport? report,
        PageTextSource? texts,
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

        return FindCoreAsync(document, term, options, report, texts, cancellationToken);
    }

    /// <summary>
    /// Makes a mark of a hit. <see cref="RedactionMark.Term"/> is the matched text of the hit (<see cref="RedactionHit.Text"/>), exactly as
    /// the page has it, and never <paramref name="term"/>: the check compares letter for letter, so a query "müller" that matched
    /// "MÜLLER" must remove and verify "MÜLLER". With <paramref name="everywhere"/> every hit has its own mark with its own text, so each
    /// spelling the search found is checked to be gone from the whole document. <paramref name="term"/> only has to be a query
    /// (not empty); it is not stored.
    /// </summary>
    /// <remarks>
    /// With <see cref="SearchOptions.WholeWord"/> <paramref name="everywhere"/> is ignored (the mark is made without it): the check
    /// counts substrings, so "gone from the whole document" would also demand that "Liste" loses its "ist", and the result would be
    /// refused. Every whole-word hit is still marked.
    /// </remarks>
    /// <param name="hit">The hit.</param>
    /// <param name="term">The query the hit came from.</param>
    /// <param name="everywhere">Whether the text must be gone from the whole document (never with whole words).</param>
    /// <param name="options">The options the search ran with.</param>
    /// <returns>A mark of the kind <see cref="RedactionKind.Hit"/> with a new id.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="RedactionException">The hit is not <see cref="RedactionHit.Markable"/> (<see cref="RedactionError.InvalidArea"/>), or the query is empty.</exception>
    public static RedactionMark ToMark(RedactionHit hit, string term, bool everywhere, SearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(hit);
        ArgumentNullException.ThrowIfNull(term);
        if (string.IsNullOrWhiteSpace(term))
        {
            throw new RedactionException(RedactionError.EmptyTerm, "The term is empty or only white space.");
        }

        if (!hit.Markable)
        {
            throw new RedactionException(RedactionError.InvalidArea, hit.NotMarkableReason ?? "The hit cannot be marked.");
        }

        return MarkOf(hit, everywhere && !options.WholeWord);
    }

    /// <summary>
    /// The mark of a markable hit. A pattern hit is exactly the place the scanner found: the pattern finds every place it can
    /// recognise, so its mark never demands "gone from the whole document" (a copy the scanner could not recognise must not make
    /// the check fail), whatever <paramref name="everywhere"/> says.
    /// </summary>
    private static RedactionMark MarkOf(RedactionHit hit, bool everywhere) =>
        new(Guid.NewGuid(), RedactionKind.Hit, hit.Page, [.. hit.Areas], hit.Text, hit.Pattern is null && everywhere, null, hit.Pattern);

    /// <summary>
    /// Makes marks of all hits that can become one, for <see cref="RedactionPlan.WithRange"/>; the others are counted, not made
    /// (<see cref="FindAsync(PdfDocument, string, SearchOptions, CancellationToken)"/> yields them with
    /// <see cref="RedactionHit.Markable"/> <see langword="false"/> and a reason, so a list can show them). Enumerates the hits once.
    /// See <see cref="ToMark"/> for <paramref name="options"/>.
    /// </summary>
    /// <param name="hits">The hits.</param>
    /// <param name="term">The query the hits came from.</param>
    /// <param name="everywhere">Whether each text must be gone from the whole document (never with whole words).</param>
    /// <param name="options">The options the search ran with.</param>
    /// <param name="notMarkable">How many hits were left out because they are not <see cref="RedactionHit.Markable"/>.</param>
    /// <returns>The marks in the order of the hits.</returns>
    public static IReadOnlyList<RedactionMark> ToMarks(IEnumerable<RedactionHit> hits, string term, bool everywhere, SearchOptions options, out int notMarkable) =>
        ToMarksCore(hits, term, everywhere, options, out notMarkable);

    /// <summary>
    /// Makes marks of the hits of a pattern search (<see cref="RedactionPatternSearch"/>), which have no query: every mark carries
    /// <see cref="RedactionMark.Pattern"/> and is never "everywhere". Hits that are not <see cref="RedactionHit.Markable"/> are counted, not made.
    /// Hits whose checksum failed (<see cref="PatternCertainty.ChecksumFailed"/>) are markable and become marks too: the caller (the
    /// application, the command line) decides whether to drop them before it calls this, as this method does not look at the certainty.
    /// Only hits of a pattern search belong here: a hit of a term (<see cref="RedactionHit.Pattern"/> <see langword="null"/>) needs its
    /// query and the choice "everywhere", so it is refused instead of being turned into a mark that would silently differ from the others.
    /// </summary>
    /// <param name="hits">The hits of <see cref="RedactionPatternSearch.FindAsync"/>.</param>
    /// <param name="notMarkable">How many hits were left out because they are not <see cref="RedactionHit.Markable"/>.</param>
    /// <returns>The marks in the order of the hits.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="hits"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A hit is not a pattern hit; nothing is returned.</exception>
    public static IReadOnlyList<RedactionMark> ToMarks(IEnumerable<RedactionHit> hits, out int notMarkable)
    {
        ArgumentNullException.ThrowIfNull(hits);
        var marks = new List<RedactionMark>();
        notMarkable = 0;
        foreach (var hit in hits)
        {
            if (hit.Pattern is null)
            {
                throw new ArgumentException("Only hits of a pattern search can be turned into marks without a query; use the overload with the query for the hits of a term.", nameof(hits));
            }

            if (hit.Markable)
            {
                marks.Add(MarkOf(hit, everywhere: false));
            }
            else
            {
                notMarkable++;
            }
        }

        return marks;
    }

    private static List<RedactionMark> ToMarksCore(IEnumerable<RedactionHit> hits, string term, bool everywhere, SearchOptions options, out int notMarkable)
    {
        ArgumentNullException.ThrowIfNull(hits);
        var marks = new List<RedactionMark>();
        notMarkable = 0;
        foreach (var hit in hits)
        {
            if (hit.Markable)
            {
                marks.Add(ToMark(hit, term, everywhere, options));
            }
            else
            {
                notMarkable++;
            }
        }

        return marks;
    }

    /// <summary>
    /// Makes the hit of a stretch of the characters of a page, the way the search makes one of a match: the text as the search
    /// has it (a line end is one space, a dropped line-end hyphen is left out), one rectangle per line, and
    /// <see cref="RedactionHit.Markable"/> telling whether it can become a mark. Blanks and line ends at the two ends of the
    /// stretch are left out (a selection that was dragged over the end of a line would otherwise carry a space as its text). The
    /// stretch is clamped to the page. This is how a text selection on the page becomes a mark.
    /// </summary>
    /// <param name="pageText">The text of the page.</param>
    /// <param name="page">The 0-based page.</param>
    /// <param name="start">The first character of the stretch (inclusive).</param>
    /// <param name="end">The end of the stretch (exclusive), as a caret position.</param>
    /// <returns>The hit, or <see langword="null"/> when nothing but blanks is in the stretch.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pageText"/> is <see langword="null"/>.</exception>
    public static RedactionHit? HitOfRange(PageText pageText, int page, int start, int end)
    {
        ArgumentNullException.ThrowIfNull(pageText);
        var chars = pageText.Chars;
        start = Math.Max(0, start);
        end = Math.Min(end, chars.Count);
        while (start < end && IsBlank(chars[start]))
        {
            start++;
        }

        while (end > start && IsBlank(chars[end - 1]))
        {
            end--;
        }

        if (start >= end)
        {
            return null;
        }

        var snippet = TextMatcher.SnippetOf(chars, start, end - 1);
        var areas = AreasOf(chars, start, end - 1, out var lossy);
        return Make(RedactionHitSource.PageText, page, areas, snippet, null, false, lossy);
    }

    private static bool IsBlank(TextChar c) =>
        c.IsLineBreak || c.CodePoint is '\r' or '\n' || (Rune.IsValid(c.CodePoint) && Rune.IsWhiteSpace(new Rune(c.CodePoint)));

    private static IAsyncEnumerable<RedactionHit> FindCoreAsync(
        PdfDocument document,
        string term,
        SearchOptions options,
        RedactionSearchReport? report,
        PageTextSource? texts,
        CancellationToken cancellationToken) =>
        FindInSourcesAsync(document, new TermMatcher(term, options), report, texts, cancellationToken);

    /// <summary>
    /// How a search finds its hits in the three kinds of source. The walk over pages, annotations and form fields, the report of what
    /// could not be read, the limits and the rectangles of a hit are shared (<see cref="FindInSourcesAsync"/>); only the question
    /// "where in this text" differs between the search for a term and the search for patterns.
    /// </summary>
    internal interface IHitMatcher
    {
        /// <summary>Finds hits in the text of a page; at most <paramref name="budget"/> (the walk needs no more).</summary>
        Task<List<RedactionHit>> FindInPageTextAsync(PdfDocument document, int page, PageText pageText, int budget, CancellationToken cancellationToken);

        /// <summary>Finds hits in the text of an annotation or a form field (already cut to <see cref="RedactionSearch.MaxSourceTextLength"/>).</summary>
        List<RedactionHit> FindInText(RedactionHitSource source, int page, string text, PageRect area, string detail);
    }

    private sealed class TermMatcher(string term, SearchOptions options) : IHitMatcher
    {
        public async Task<List<RedactionHit>> FindInPageTextAsync(PdfDocument document, int page, PageText pageText, int budget, CancellationToken cancellationToken)
        {
            var found = TextMatcher.Find(pageText.Chars, term, options);
            if (found.Count == 0)
            {
                return [];
            }

            var take = Math.Min(found.Count, budget);
            IReadOnlyList<TextMatch> matches = found.Count == take ? found : [.. found.Take(take)];
            return await PageTextHitsAsync(document, page, pageText, matches, cancellationToken).ConfigureAwait(false);
        }

        public List<RedactionHit> FindInText(RedactionHitSource source, int page, string text, PageRect area, string detail)
        {
            var chars = RuneChars(text);
            var hits = new List<RedactionHit>();
            foreach (var (first, last) in TextMatcher.Find(chars, term, options))
            {
                hits.Add(TextHit(source, page, chars, area, detail, first, last));
            }

            return hits;
        }
    }

    /// <summary>The walk shared by the search for a term and the search for patterns.</summary>
    internal static async IAsyncEnumerable<RedactionHit> FindInSourcesAsync(
        PdfDocument document,
        IHitMatcher matcher,
        RedactionSearchReport? report,
        PageTextSource? texts,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var total = 0;
        var pageCount = document.PageCount;
        for (var page = 0; page < pageCount; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            PageText? pageText = null;
            try
            {
                pageText = texts is null
                    ? await document.GetPageTextAsync(page, PdfPriority.Background, cancellationToken).ConfigureAwait(false)
                    : await texts.GetAsync(page, PdfPriority.Background, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                report?.MarkIncomplete(page);
            }

            if (pageText is not null)
            {
                var pageHits = await matcher.FindInPageTextAsync(document, page, pageText, MaxHits - total, cancellationToken).ConfigureAwait(false);
                if (pageHits.Count > 0)
                {
                    foreach (var hit in pageHits)
                    {
                        total++;
                        yield return hit;
                    }

                    if (total >= MaxHits)
                    {
                        EndAtLimit(report);
                        yield break;
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<PdfAnnotation> annotations = [];
            try
            {
                var read = await document.GetAnnotationsWithStatusAsync(page, cancellationToken).ConfigureAwait(false);
                annotations = read.Annotations;
                if (read.Incomplete)
                {
                    report?.MarkIncomplete(page);
                }
            }
            catch (InvalidDataException)
            {
                report?.MarkIncomplete(page);
            }

            foreach (var annotation in annotations)
            {
                if (string.IsNullOrEmpty(annotation.Contents))
                {
                    continue;
                }

                foreach (var hit in matcher.FindInText(RedactionHitSource.Annotation, page, CutSourceText(annotation.Contents, page, report), annotation.Bounds, annotation.Subtype))
                {
                    total++;
                    yield return hit;
                    if (total >= MaxHits)
                    {
                        EndAtLimit(report);
                        yield break;
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<FormField> fields = [];
            try
            {
                var read = await document.GetFormFieldsForSearchAsync(page, cancellationToken).ConfigureAwait(false);
                fields = read.Fields;
                if (read.WidgetsLeftOut)
                {
                    report?.MarkIncomplete(page);
                }
            }
            catch (InvalidDataException)
            {
                report?.MarkIncomplete(page);
            }

            foreach (var field in fields)
            {
                // A widget Pagewing could not read (value too long, too slow, too many options, no name) comes with no value at all.
                // A signature and a push button hold no text to look for.
                if (field.Lock == FormLock.Unsupported && field.Kind is not (FormFieldKind.Signature or FormFieldKind.PushButton))
                {
                    report?.MarkIncomplete(page);
                }

                foreach (var value in ValuesOf(field))
                {
                    foreach (var hit in matcher.FindInText(RedactionHitSource.FormField, page, CutSourceText(value, page, report), field.Bounds, field.Key.FieldName))
                    {
                        total++;
                        yield return hit;
                        if (total >= MaxHits)
                        {
                            EndAtLimit(report);
                            yield break;
                        }
                    }
                }
            }
        }
    }

    private static void EndAtLimit(RedactionSearchReport? report)
    {
        if (report is not null)
        {
            report.LimitReached = true;
        }
    }

    internal static async Task<List<RedactionHit>> PageTextHitsAsync(
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
            var areas = AreasOf(chars, first, last, out var lossy);
            hits.Add(Make(RedactionHitSource.PageText, page, areas, snippet, null, invisible[i], lossy));
        }

        return hits;
    }

    /// <summary>Cuts the text of an annotation or a form field to <see cref="MaxSourceTextLength"/> and marks the page as incomplete when it was cut.</summary>
    private static string CutSourceText(string text, int page, RedactionSearchReport? report)
    {
        if (text.Length <= MaxSourceTextLength)
        {
            return text;
        }

        report?.MarkIncomplete(page);
        return text[..MaxSourceTextLength];
    }

    /// <summary>The characters of a text without boxes (one per code point), as the matcher and the snippet read a source that is not page text.</summary>
    internal static List<TextChar> RuneChars(string text)
    {
        var chars = new List<TextChar>(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            chars.Add(new TextChar(rune.Value, default, TextCharTraits.None, 0));
        }

        return chars;
    }

    /// <summary>The hit of the characters first to last of an annotation or a form field; the area is the whole annotation or widget.</summary>
    internal static RedactionHit TextHit(RedactionHitSource source, int page, IReadOnlyList<TextChar> chars, PageRect area, string detail, int first, int last) =>
        Make(source, page, IsUsable(area) ? [area] : [], TextMatcher.SnippetOf(chars, first, last), detail, false, lossy: false);

    internal static RedactionHit Make(RedactionHitSource source, int page, List<PageRect> areas, SearchSnippet snippet, string? detail, bool invisible, bool lossy = false)
    {
        var text = snippet.Match;
        string? reason = null;
        if (areas.Count == 0)
        {
            reason = "No character of the hit has a box with an area, so there is nothing to mark.";
        }
        else if (lossy)
        {
            reason = "A letter or digit of the hit has no box with an area; marking the rest would leave part of the text in place.";
        }
        else if (areas.Count > RedactionLimits.MaxAreasPerMark)
        {
            reason = "The hit has more lines than a mark can hold.";
        }
        else if (string.IsNullOrWhiteSpace(text))
        {
            reason = "The matched text is only white space.";
        }
        else if (text.Length > RedactionLimits.MaxTermLength)
        {
            reason = "The matched text is longer than a term can be.";
        }

        return new RedactionHit(source, page, areas, text, invisible) { Markable = reason is null, Snippet = snippet, Detail = detail, NotMarkableReason = reason };
    }

    /// <summary>
    /// The rectangles of the characters first to last, one per line, like <see cref="PageText.RectsOf"/>, but a character whose box has no
    /// area or is not finite (a zero-width joiner, a combining mark, a box the engine could not read) is left out: it would not
    /// be a valid rectangle, and in a union it would stretch the rectangle to the corner of the page. A line with no usable box gives
    /// no rectangle. <paramref name="lossy"/> is set when a character that was left out is more than white space, a format character or a
    /// combining mark (a letter, digit or sign): the rectangles then do not cover the whole hit, and the hit must not be marked.
    /// </summary>
    internal static List<PageRect> AreasOf(IReadOnlyList<TextChar> chars, int first, int last, out bool lossy)
    {
        var rects = new List<PageRect>();
        lossy = false;
        var end = last + 1;
        var i = first;
        while (i < end)
        {
            var segmentEnd = Math.Min(TextGeometry.SegmentEnd(chars, i), end);
            var from = -1;
            var to = -1;
            for (var k = i; k < segmentEnd; k++)
            {
                if (chars[k].IsGenerated)
                {
                    continue;
                }

                if (IsUsable(chars[k].Box))
                {
                    from = from < 0 ? k : from;
                    to = k;
                }
                else if (!IsIgnorable(chars[k]))
                {
                    lossy = true;
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

    /// <summary>Whether a character with no area leaves nothing visible out: white space, a format character (such as a zero-width joiner) or a combining mark.</summary>
    private static bool IsIgnorable(TextChar c)
    {
        if (!Rune.IsValid(c.CodePoint))
        {
            return false;
        }

        var rune = new Rune(c.CodePoint);
        return Rune.IsWhiteSpace(rune)
            || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
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

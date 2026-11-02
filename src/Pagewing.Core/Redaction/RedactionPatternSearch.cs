using System.Text;
using Pagewing.Core.Pdf;
using Pagewing.Core.Text;

namespace Pagewing.Core.Redaction;

/// <summary>
/// "Search and redact" for patterns (milestone 14.1): finds e-mail addresses, IBANs, telephone numbers and payment card numbers in
/// everything that can hold them, in the page text, the texts of annotations and the values of form fields. It walks the same sources
/// the same way as <see cref="RedactionSearch"/> (same limits, same report of what could not be read, same rectangles per line), and
/// only asks <see cref="RedactionPatterns"/> instead of the text matcher. A pattern never crosses a line break: the page text is
/// scanned with a barrier at every line end, so a number split over two lines is not found (<see cref="RedactionSearch.NotSearched"/> says so).
/// </summary>
public static class RedactionPatternSearch
{
    /// <summary>
    /// Finds the patterns of the given kinds on all pages, page by page. Every hit has <see cref="RedactionHit.Pattern"/> and
    /// <see cref="RedactionHit.Certainty"/>; <see cref="RedactionHit.Text"/> is the matched text as the page has it (generated
    /// spaces between groups included). The search stops after <see cref="RedactionSearch.MaxHits"/> hits and then says so in the
    /// <paramref name="report"/>; a page or source that could not be read completely is listed there too, never taken for "no hits".
    /// </summary>
    /// <param name="document">The document to search.</param>
    /// <param name="kinds">The kinds to look for; none finds nothing.</param>
    /// <param name="report">Receives what could not be searched; may be <see langword="null"/>, but then nothing says that a page was left out.</param>
    /// <param name="texts">The cache of page texts, which must belong to <paramref name="document"/>; <see langword="null"/> reads every page from the document.</param>
    /// <param name="cancellationToken">Stops the search before the next page.</param>
    /// <returns>The hits in page order; in page text order within a page, then annotations, then form fields.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> or <paramref name="kinds"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static IAsyncEnumerable<RedactionHit> FindAsync(
        PdfDocument document,
        IReadOnlySet<RedactionPatternKind> kinds,
        RedactionSearchReport? report,
        PageTextSource? texts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(kinds);
        return kinds.Count == 0
            ? AsyncEnumerable.Empty<RedactionHit>()
            : RedactionSearch.FindInSourcesAsync(document, new PatternMatcher(new HashSet<RedactionPatternKind>(kinds)), report, texts, cancellationToken);
    }

    private sealed class PatternMatcher(IReadOnlySet<RedactionPatternKind> kinds) : RedactionSearch.IHitMatcher
    {
        public async Task<List<RedactionHit>> FindInPageTextAsync(PdfDocument document, int page, PageText pageText, int budget, CancellationToken cancellationToken)
        {
            var scan = ScanText.Of(pageText.Chars);
            var found = RedactionPatterns.Scan(scan.Text, kinds);
            if (found.Count == 0)
            {
                return [];
            }

            var take = Math.Min(found.Count, budget);
            var matches = new List<TextMatch>(take);
            for (var i = 0; i < take; i++)
            {
                matches.Add(new TextMatch(scan.CharOf[found[i].Start], scan.CharOf[found[i].End - 1]));
            }

            var hits = await RedactionSearch.PageTextHitsAsync(document, page, pageText, matches, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < hits.Count; i++)
            {
                hits[i] = hits[i] with { Pattern = found[i].Kind, Certainty = found[i].Certainty };
            }

            return hits;
        }

        public List<RedactionHit> FindInText(RedactionHitSource source, int page, string text, PageRect area, string detail)
        {
            var found = RedactionPatterns.Scan(text, kinds);
            if (found.Count == 0)
            {
                return [];
            }

            // The positions of the scanner are UTF-16 units; the snippet and the match read code points.
            var chars = RedactionSearch.RuneChars(text);
            var runeOf = new int[text.Length];
            var unit = 0;
            for (var rune = 0; rune < chars.Count; rune++)
            {
                for (var k = new Rune(chars[rune].CodePoint).Utf16SequenceLength; k > 0; k--)
                {
                    runeOf[unit++] = rune;
                }
            }

            var hits = new List<RedactionHit>(found.Count);
            foreach (var match in found)
            {
                var hit = RedactionSearch.TextHit(source, page, chars, area, detail, runeOf[match.Start], runeOf[match.End - 1]);
                hits.Add(hit with { Pattern = match.Kind, Certainty = match.Certainty });
            }

            return hits;
        }
    }

    /// <summary>
    /// The text of a page for the pattern scanner and, for every UTF-16 unit of it, the character it comes from. White space that
    /// the engine generated between words is a space; a line end is a line feed, and so is the end of a line that carries a hyphen
    /// (the engine reports such a hyphen as one character without a line break). The scanner treats a line feed as a character of no
    /// pattern, so nothing is found across it.
    /// </summary>
    private readonly struct ScanText
    {
        private ScanText(string text, int[] charOf)
        {
            Text = text;
            CharOf = charOf;
        }

        public string Text { get; }

        public int[] CharOf { get; }

        public static ScanText Of(IReadOnlyList<TextChar> chars)
        {
            var text = new StringBuilder(chars.Count + 16);
            var charOf = new List<int>(chars.Count + 16);

            void Append(int codePoint, int index)
            {
                var rune = Rune.IsValid(codePoint) ? new Rune(codePoint) : Rune.ReplacementChar;
                Span<char> units = stackalloc char[2];
                var length = rune.EncodeToUtf16(units);
                text.Append(units[..length]);
                for (var k = 0; k < length; k++)
                {
                    charOf.Add(index);
                }
            }

            for (var i = 0; i < chars.Count; i++)
            {
                var c = chars[i];
                if (c.IsGenerated)
                {
                    Append(c.IsLineBreak ? '\n' : ' ', i);
                    continue;
                }

                Append(c.CodePoint, i);
                if ((c.Flags & TextCharTraits.Hyphen) != 0)
                {
                    Append('\n', i);
                }
            }

            return new ScanText(text.ToString(), [.. charOf]);
        }
    }
}

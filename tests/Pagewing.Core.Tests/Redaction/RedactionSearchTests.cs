using System.Text;
using Pagewing.Core.Pdf;
using Pagewing.Core.Redaction;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Text;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Redaction;

/// <summary>"Search and redact" (milestone 14, task 9): the hits in page text, annotations and form values, and the marks made of them.</summary>
public sealed class RedactionSearchTests
{
    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<RedactionHit>> FindAsync(PdfDocument document, string term, SearchOptions options = default, PageTextSource? texts = null, ICollection<int>? incomplete = null)
    {
        var hits = new List<RedactionHit>();
        await foreach (var hit in RedactionSearch.FindAsync(document, term, options, texts, incomplete, Ct))
        {
            hits.Add(hit);
        }

        return hits;
    }

    private async Task<PdfDocument> OpenAsync(string name) => await engine.OpenAsync(TestData.Pdf(name), cancellationToken: Ct);

    private async Task<PdfDocument> OpenBytesAsync(TempFolder folder, byte[] bytes)
    {
        var path = Path.Combine(folder.Path, Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, bytes);
        return await engine.OpenAsync(path, cancellationToken: Ct);
    }

    private static string Utf16(string text) => "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text)) + ">";

    /// <summary>One page (300 x 200) with the given content and one annotation per text (a note with this comment).</summary>
    private static byte[] WithNotes(string content, params string[] notes)
    {
        var pdf = new VerifierPdf(content);
        var numbers = new List<int>();
        for (var i = 0; i < notes.Length; i++)
        {
            numbers.Add(pdf.Add($"<< /Type /Annot /Subtype /Text /Rect [{10 + (30 * i)} 20 {30 + (30 * i)} 40] /Contents {Utf16(notes[i])} /F 4 >>"));
        }

        pdf.PageEntries = $"/Annots [{string.Join(' ', numbers.Select(static n => $"{n} 0 R"))}]";
        return pdf.Build();
    }

    private static string PageWith(string text) => $"BT /F1 12 Tf 20 150 Td ({text}) Tj ET";

    [Fact]
    public async Task FindsTheTermAcrossAHyphenatedLineBreak()
    {
        await using var document = await OpenAsync("text-rich.pdf");

        var hits = await FindAsync(document, "Windows");

        var hit = Assert.Single(hits);
        Assert.Equal(RedactionHitSource.PageText, hit.Source);
        Assert.Equal(0, hit.Page);
        Assert.Equal("Windows", hit.Text);
        Assert.False(hit.Invisible);
        Assert.True(hit.Markable);
        Assert.Equal(2, hit.Areas.Count);
        Assert.True(hit.Areas[0].Top < hit.Areas[1].Top);
        Assert.All(hit.Areas, static a => Assert.True(a.Width > 0 && a.Height > 0));

        // The same text as a hyphenated query on one line would not be found ("Win-dows" is not what the page says once it is joined).
        Assert.Empty(await FindAsync(document, "Win-dows"));
    }

    [Fact]
    public async Task FindsInvisibleOcrTextAndFlagsIt()
    {
        using var folder = new TempFolder();
        var content = "BT /F1 12 Tf 20 150 Td 3 Tr (Hidden Zephyr) Tj ET BT /F1 12 Tf 20 100 Td 0 Tr (Shown Zephyr) Tj ET BT /F1 12 Tf 20 50 Td 3 Tr (Mixed ) Tj 0 Tr (Zephyr) Tj ET";
        await using var document = await OpenBytesAsync(folder, new VerifierPdf(content).Build());

        var hits = await FindAsync(document, "Zephyr");

        Assert.Equal(3, hits.Count);
        Assert.All(hits, static h => Assert.Equal(RedactionHitSource.PageText, h.Source));
        var hidden = hits.Single(static h => h.Areas[0].Top < 60);
        Assert.True(hidden.Invisible);
        Assert.True(hidden.Markable);
        Assert.False(hits.Single(static h => h.Areas[0].Top > 60 && h.Areas[0].Top < 110).Invisible);
        Assert.False(hits.Single(static h => h.Areas[0].Top > 110).Invisible);
    }

    [Fact]
    public async Task FindsInAnnotationsAndFormValuesAsOwnSources()
    {
        await using var annotated = await OpenAsync("annotations-foreign.pdf");
        var notes = await FindAsync(annotated, "Check this");

        var note = Assert.Single(notes);
        Assert.Equal(RedactionHitSource.Annotation, note.Source);
        Assert.Equal(0, note.Page);
        Assert.Equal("Check this", note.Text);
        Assert.Equal("Highlight", note.Detail);
        Assert.False(note.Invisible);
        var area = Assert.Single(note.Areas);
        var bounds = (await annotated.GetAnnotationsAsync(0, Ct)).Single(static a => a.Contents == "Check this sentence").Bounds;
        Assert.Equal(bounds, area);

        await using var form = await OpenAsync("forms-basic.pdf");
        var values = await FindAsync(form, "Fest");

        var value = Assert.Single(values, static h => h.Source == RedactionHitSource.FormField);
        Assert.Equal("Fest", value.Text);
        Assert.Equal("Gesperrt", value.Detail);
        var widget = Assert.Single(value.Areas);
        Assert.True(widget.Width > 0 && widget.Height > 0);
        Assert.True(value.Markable);
        Assert.DoesNotContain(values, static h => h.Source == RedactionHitSource.Annotation);
    }

    [Fact]
    public async Task CaseAndWholeWordOptionsBehaveLikeTheSearchPanel()
    {
        await using var document = await OpenAsync("text-rich.pdf");

        foreach (var (query, options) in new (string, SearchOptions)[]
        {
            ("pagewing", default),
            ("pagewing", new SearchOptions(MatchCase: true)),
            ("Pagewing", new SearchOptions(MatchCase: true)),
            ("Pagewi", default),
            ("Pagewi", new SearchOptions(WholeWord: true)),
            ("Pagewing", new SearchOptions(MatchCase: true, WholeWord: true)),
            ("ist", new SearchOptions(WholeWord: true)),
        })
        {
            var expected = 0;
            await foreach (var page in DocumentSearch.SearchAsync(document, query, options, 0, Ct))
            {
                expected += page.Hits.Count;
            }

            var hits = (await FindAsync(document, query, options)).Where(static h => h.Source == RedactionHitSource.PageText).ToList();
            Assert.Equal(expected, hits.Count);
        }

        Assert.Equal(4, (await FindAsync(document, "pagewing")).Count(static h => h.Source == RedactionHitSource.PageText));
        Assert.Equal(3, (await FindAsync(document, "Pagewing", new SearchOptions(MatchCase: true))).Count(static h => h.Source == RedactionHitSource.PageText));
        Assert.Empty(await FindAsync(document, "Pagewi", new SearchOptions(WholeWord: true)));
    }

    [Fact]
    public async Task UnicodeFormsMatchTheSameWay()
    {
        using var folder = new TempFolder();
        const string Notes = "Müller|MÜLLER|Müller|Weiße|WEIẞE|שלום עולם|😀 Smiley|état";
        await using var document = await OpenBytesAsync(folder, WithNotes(PageWith("Plain"), Notes.Split('|')));

        // Case folded: the precomposed, the capitals and the decomposed spelling all match; the text of the hit is what the page has.
        var müller = await FindAsync(document, "müller");
        Assert.Equal(["Müller", "MÜLLER", "Müller"], müller.Select(static h => h.Text));
        Assert.All(müller, static h => Assert.Equal(RedactionHitSource.Annotation, h.Source));

        // With the case: the capitals do not match, a decomposed ü still does (normalization form KC).
        Assert.Equal(["Müller", "Müller"], (await FindAsync(document, "Müller", new SearchOptions(MatchCase: true))).Select(static h => h.Text));
        Assert.Equal(["MÜLLER"], (await FindAsync(document, "MÜLLER", new SearchOptions(MatchCase: true))).Select(static h => h.Text));

        // The decomposed query finds the precomposed spelling.
        Assert.Equal(3, (await FindAsync(document, "Müller")).Count);

        // Sharp s: the capital form is the same letter without the case.
        Assert.Equal(["Weiße", "WEIẞE"], (await FindAsync(document, "weiße")).Select(static h => h.Text));
        Assert.Equal(["WEIẞE"], (await FindAsync(document, "WEIẞE", new SearchOptions(MatchCase: true))).Select(static h => h.Text));

        // Right to left text, an emoji (a surrogate pair), and a letter with a combining accent in the middle of a word.
        Assert.Equal(["עולם"], (await FindAsync(document, "עולם")).Select(static h => h.Text));
        Assert.Equal(["😀"], (await FindAsync(document, "😀")).Select(static h => h.Text));
        Assert.Equal(["état"], (await FindAsync(document, "état")).Select(static h => h.Text));

        // A single character matches in all three spellings of the umlaut; a query with a space in the middle of a word matches nothing.
        Assert.Equal(3, (await FindAsync(document, "ü")).Count);
        Assert.Empty(await FindAsync(document, "Mü ller"));
    }

    [Fact]
    public async Task RegexCharactersAreLiteral()
    {
        using var folder = new TempFolder();
        await using var document = await OpenBytesAsync(folder, new VerifierPdf(PageWith("a.c abc x+y [1] .* (2) a|b ^$")).Build());

        Assert.Equal(["a.c"], (await FindAsync(document, "a.c")).Select(static h => h.Text));
        Assert.Equal(["x+"], (await FindAsync(document, "x+")).Select(static h => h.Text));
        Assert.Equal(["[1]"], (await FindAsync(document, "[1]")).Select(static h => h.Text));
        Assert.Equal([".*"], (await FindAsync(document, ".*")).Select(static h => h.Text));
        Assert.Equal(["a|b"], (await FindAsync(document, "a|b")).Select(static h => h.Text));
        Assert.Equal(["^$"], (await FindAsync(document, "^$")).Select(static h => h.Text));
        Assert.Empty(await FindAsync(document, "a*"));
        Assert.Empty(await FindAsync(document, "^a"));
        Assert.Empty(await FindAsync(document, "(.)"));
        Assert.Empty(await FindAsync(document, "\\d"));
    }

    [Fact]
    public async Task NoMatchReturnsNothing()
    {
        await using var document = await OpenAsync("text-rich.pdf");
        var incomplete = new List<int>();

        Assert.Empty(await FindAsync(document, "Zebrastreifen", incomplete: incomplete));
        Assert.Empty(incomplete);
    }

    [Fact]
    public async Task AnEmptyOrTooLongTermIsRefusedByTheCallAndNotByTheEnumeration()
    {
        await using var document = await OpenAsync("text-rich.pdf");

        foreach (var term in new[] { string.Empty, "   ", "\t\n" })
        {
            var error = Assert.Throws<RedactionException>(() => RedactionSearch.FindAsync(document, term, default, Ct));
            Assert.Equal(RedactionError.EmptyTerm, error.Error);
        }

        var tooLong = Assert.Throws<RedactionException>(() => RedactionSearch.FindAsync(document, new string('a', RedactionLimits.MaxTermLength + 1), default, Ct));
        Assert.Equal(RedactionError.LimitExceeded, tooLong.Error);
        Assert.Throws<ArgumentNullException>(() => RedactionSearch.FindAsync(document, null!, default, Ct));
        Assert.NotNull(RedactionSearch.FindAsync(document, new string('a', RedactionLimits.MaxTermLength), default, Ct));
    }

    [Fact]
    public async Task CancelStopsBetweenPages()
    {
        await using var document = await OpenAsync("text-dense.pdf");
        using var source = new PageTextSource(document);
        var loaded = 0;
        source.Loaded += _ => Interlocked.Increment(ref loaded);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var seen = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var hit in RedactionSearch.FindAsync(document, "Needle", default, source, null, cancel.Token))
            {
                seen++;
                cancel.Cancel();
            }
        });

        Assert.Equal(1, seen);
        Assert.InRange(Volatile.Read(ref loaded), 1, 20);
    }

    [Fact]
    public async Task TwoHundredPagesStayCheap()
    {
        await using var document = await OpenAsync("text-dense.pdf");
        Assert.Equal(200, document.PageCount);
        using var source = new PageTextSource(document);
        var loaded = 0;
        source.Loaded += _ => Interlocked.Increment(ref loaded);
        var incomplete = new List<int>();

        var hits = await FindAsync(document, "Needle", texts: source, incomplete: incomplete);

        Assert.Equal(20, hits.Count);
        Assert.All(hits, static h => Assert.Equal(RedactionHitSource.PageText, h.Source));
        Assert.Equal(200, Volatile.Read(ref loaded));
        Assert.Equal(PageTextSource.MaxCachedPages, source.Count);
        Assert.Empty(incomplete);

        // The cache holds the most recent pages only (the last ones of the sweep), not all 200.
        Assert.True(source.TryGet(199, out _));
        Assert.False(source.TryGet(0, out _));
    }

    [Fact]
    public async Task TheMarkTermIsTheMatchedPageTextNotTheQuery()
    {
        using var folder = new TempFolder();
        await using var document = await OpenBytesAsync(folder, new VerifierPdf(PageWith("Herr MÜLLER und Frau Müller")).Build());

        var hits = await FindAsync(document, "müller");
        var upper = hits.Single(static h => h.Text == "MÜLLER");

        var mark = RedactionSearch.ToMark(upper, "müller", everywhere: true);

        Assert.Equal("MÜLLER", mark.Term);
        Assert.Equal(RedactionKind.Hit, mark.Kind);
        Assert.True(mark.Everywhere);
        Assert.Equal(0, mark.Page);
        Assert.Equal(upper.Areas, mark.Areas);
        Assert.NotEqual(Guid.Empty, mark.Id);

        // Every spelling that was found is its own mark with its own text: nothing of the other case is left unchecked.
        var marks = RedactionSearch.ToMarks(hits, "müller", everywhere: true, out var skipped);
        Assert.Equal(0, skipped);
        Assert.Equal(["MÜLLER", "Müller"], marks.Select(static m => m.Term).Order(StringComparer.Ordinal));
        Assert.Equal(2, RedactionPlan.Empty.WithRange(marks).Marks.Count);

        // The query is only checked: an empty one is refused.
        Assert.Equal(RedactionError.EmptyTerm, Assert.Throws<RedactionException>(() => RedactionSearch.ToMark(upper, " ", false)).Error);
    }

    [Fact]
    public async Task DegenerateBoxesAreDroppedOrTheHitIsNotMarkable()
    {
        // A box without an area (a zero-width joiner, a combining mark), one that is not a number, and one the engine could not read
        // (all zero) must neither become a rectangle nor stretch the line to the corner of the page.
        static TextChar Char(char c, PageRect box) => new(c, box, TextCharTraits.None, 12);
        var good1 = new PageRect(100, 50, 110, 62);
        var good2 = new PageRect(110, 50, 120, 62);
        var chars = new[]
        {
            Char('a', good1),
            Char('‍', new PageRect(110, 50, 110, 62)),
            Char('b', default),
            Char('c', new PageRect(double.NaN, 50, 120, 62)),
            Char('d', good2),
        };

        var areas = RedactionSearch.AreasOf(chars, 0, 4);

        Assert.Equal([good1.Union(good2)], areas);

        // Every box degenerate: no rectangle, and the hit says it cannot be marked instead of throwing.
        var none = RedactionSearch.AreasOf([Char('‍', default), Char('́', new PageRect(5, 5, 5, 5))], 0, 1);
        Assert.Empty(none);
        var hit = RedactionSearch.Make(RedactionHitSource.PageText, 0, none, new SearchSnippet(string.Empty, "x", string.Empty), null, invisible: false);
        Assert.False(hit.Markable);
        Assert.Equal(RedactionError.InvalidArea, Assert.Throws<RedactionException>(() => RedactionSearch.ToMark(hit, "x", false)).Error);
        Assert.Empty(RedactionSearch.ToMarks([hit], "x", false, out var skipped));
        Assert.Equal(1, skipped);

        // Too many lines, white space only and a text longer than a term are not markable either.
        var lines = Enumerable.Range(0, RedactionLimits.MaxAreasPerMark + 1).Select(static i => new PageRect(0, i, 10, i + 0.5)).ToList();
        Assert.False(RedactionSearch.Make(RedactionHitSource.PageText, 0, lines, new SearchSnippet(string.Empty, "x", string.Empty), null, false).Markable);
        Assert.False(RedactionSearch.Make(RedactionHitSource.PageText, 0, [good1], new SearchSnippet(string.Empty, " ", string.Empty), null, false).Markable);
        Assert.False(RedactionSearch.Make(RedactionHitSource.PageText, 0, [good1], new SearchSnippet(string.Empty, new string('x', RedactionLimits.MaxTermLength + 1), string.Empty), null, false).Markable);
        Assert.True(RedactionSearch.Make(RedactionHitSource.PageText, 0, [good1], new SearchSnippet(string.Empty, new string('x', RedactionLimits.MaxTermLength), string.Empty), null, false).Markable);

        // Through a real file: an annotation without any area is found, flagged, and a neighbour with an area is not affected.
        using var folder = new TempFolder();
        var pdf = new VerifierPdf(PageWith("Plain"));
        var flat = pdf.Add($"<< /Type /Annot /Subtype /Text /Rect [50 50 50 50] /Contents {Utf16("Zephyr flat")} /F 4 >>");
        var solid = pdf.Add($"<< /Type /Annot /Subtype /Text /Rect [80 50 100 70] /Contents {Utf16("Zephyr solid")} /F 4 >>");
        pdf.PageEntries = $"/Annots [{flat} 0 R {solid} 0 R]";
        await using var document = await OpenBytesAsync(folder, pdf.Build());

        var hits = await FindAsync(document, "Zephyr");

        Assert.Equal(2, hits.Count);
        Assert.Single(hits, static h => !h.Markable && h.Areas.Count == 0);
        Assert.Single(hits, static h => h.Markable && h.Areas.Count == 1);
    }

    [Fact]
    public async Task TheSearchEndsAtTheMarkLimit()
    {
        using var folder = new TempFolder();
        var content = string.Join(' ', Enumerable.Repeat("x", 60));
        var pages = Enumerable.Range(0, 200).Select(_ => $"BT /F1 6 Tf 5 190 Td ({content}) Tj ET").ToArray();
        await using var document = await OpenBytesAsync(folder, new VerifierPdf(pages).Build());

        var hits = await FindAsync(document, "x");

        // 200 pages with 60 hits each are 12,000 places; the search stops at the number of marks a plan can hold.
        Assert.Equal(RedactionSearch.MaxHits, hits.Count);
        Assert.Equal(RedactionLimits.MaxMarks, RedactionPlan.Empty.WithRange(RedactionSearch.ToMarks(hits, "x", false, out _)).Marks.Count);
    }
}

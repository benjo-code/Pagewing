using System.Text;
using Pagewing.Core.Pdf;
using Pagewing.Core.Redaction;
using Pagewing.Core.Saving;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Redaction;

/// <summary>"Search and redact" (milestone 14, task 9): the hits in page text, annotations and form values, and the marks made of them.</summary>
public sealed class RedactionSearchTests
{
    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<RedactionHit>> FindAsync(PdfDocument document, string term, SearchOptions options = default, PageTextSource? texts = null, RedactionSearchReport? report = null)
    {
        var hits = new List<RedactionHit>();
        await foreach (var hit in RedactionSearch.FindAsync(document, term, options, report, texts, Ct))
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
        var report = new RedactionSearchReport();

        Assert.Empty(await FindAsync(document, "Zebrastreifen", report: report));
        Assert.True(report.IsComplete);
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
            await foreach (var hit in RedactionSearch.FindAsync(document, "Needle", default, null, source, cancel.Token))
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
        var report = new RedactionSearchReport();

        var hits = await FindAsync(document, "Needle", texts: source, report: report);

        Assert.Equal(20, hits.Count);
        Assert.All(hits, static h => Assert.Equal(RedactionHitSource.PageText, h.Source));
        Assert.Equal(200, Volatile.Read(ref loaded));
        Assert.Equal(PageTextSource.MaxCachedPages, source.Count);
        Assert.True(report.IsComplete);

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

        var mark = RedactionSearch.ToMark(upper, "müller", everywhere: true, default);

        Assert.Equal("MÜLLER", mark.Term);
        Assert.Equal(RedactionKind.Hit, mark.Kind);
        Assert.True(mark.Everywhere);
        Assert.Equal(0, mark.Page);
        Assert.Equal(upper.Areas, mark.Areas);
        Assert.NotEqual(Guid.Empty, mark.Id);

        // Every spelling that was found is its own mark with its own text: nothing of the other case is left unchecked.
        var marks = RedactionSearch.ToMarks(hits, "müller", everywhere: true, default, out var skipped);
        Assert.Equal(0, skipped);
        Assert.Equal(["MÜLLER", "Müller"], marks.Select(static m => m.Term).Order(StringComparer.Ordinal));
        Assert.Equal(2, RedactionPlan.Empty.WithRange(marks).Marks.Count);

        // The query is only checked: an empty one is refused.
        Assert.Equal(RedactionError.EmptyTerm, Assert.Throws<RedactionException>(() => RedactionSearch.ToMark(upper, " ", false, default)).Error);
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
            Char('́', default),
            Char('d', good2),
        };

        var areas = RedactionSearch.AreasOf(chars, 0, 3, out var lossy);

        Assert.Equal([good1.Union(good2)], areas);
        Assert.False(lossy);

        // Every box degenerate: no rectangle, and the hit says it cannot be marked instead of throwing.
        var none = RedactionSearch.AreasOf([Char('‍', default), Char('́', new PageRect(5, 5, 5, 5))], 0, 1, out var noneLossy);
        Assert.Empty(none);
        Assert.False(noneLossy);
        var hit = RedactionSearch.Make(RedactionHitSource.PageText, 0, none, new SearchSnippet(string.Empty, "x", string.Empty), null, invisible: false);
        Assert.False(hit.Markable);
        Assert.NotNull(hit.NotMarkableReason);
        Assert.Equal(RedactionError.InvalidArea, Assert.Throws<RedactionException>(() => RedactionSearch.ToMark(hit, "x", false, default)).Error);
        Assert.Empty(RedactionSearch.ToMarks([hit], "x", false, default, out var skipped));
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
    public void ALetterWithoutABoxMakesTheHitNotMarkableInsteadOfPartlyMarked()
    {
        // "Müller" where the M has no box: the rectangle would cover "üller" and leave the M on the page.
        static TextChar Char(char c, PageRect box) => new(c, box, TextCharTraits.None, 12);
        var chars = "Müller".Select((c, i) => Char(c, c == 'M' ? default : new PageRect(100 + (10 * i), 50, 110 + (10 * i), 62))).ToList();

        var areas = RedactionSearch.AreasOf(chars, 0, 5, out var lossy);

        Assert.True(lossy);
        Assert.Single(areas);
        var hit = RedactionSearch.Make(RedactionHitSource.PageText, 0, areas, new SearchSnippet(string.Empty, "Müller", string.Empty), null, false, lossy);
        Assert.False(hit.Markable);
        Assert.Contains("letter or digit", hit.NotMarkableReason, StringComparison.Ordinal);
        Assert.Equal(RedactionError.InvalidArea, Assert.Throws<RedactionException>(() => RedactionSearch.ToMark(hit, "Müller", false, default)).Error);

        // A digit, a sign and a letter are all more than ignorable; white space, a joiner and a combining mark are not.
        foreach (var c in new[] { '7', '-', 'x' })
        {
            RedactionSearch.AreasOf([Char('a', new PageRect(0, 0, 5, 5)), Char(c, default)], 0, 1, out var one);
            Assert.True(one, c.ToString());
        }

        foreach (var c in new[] { ' ', '‍', '́', '­' })
        {
            RedactionSearch.AreasOf([Char('a', new PageRect(0, 0, 5, 5)), Char(c, default)], 0, 1, out var one);
            Assert.False(one, $"U+{(int)c:X4}");
        }
    }

    [Fact]
    public async Task TheSearchEndsAtTheMarkLimit()
    {
        using var folder = new TempFolder();
        var content = string.Join(' ', Enumerable.Repeat("x", 60));
        var pages = Enumerable.Range(0, 200).Select(_ => $"BT /F1 6 Tf 5 190 Td ({content}) Tj ET").ToArray();
        await using var document = await OpenBytesAsync(folder, new VerifierPdf(pages).Build());
        var report = new RedactionSearchReport();

        var hits = await FindAsync(document, "x", report: report);

        // 200 pages with 60 hits each are 12,000 places; the search stops at the number of marks a plan can hold and says so.
        Assert.Equal(RedactionSearch.MaxHits, hits.Count);
        Assert.True(report.LimitReached);
        Assert.False(report.IsComplete);
        Assert.Equal(RedactionLimits.MaxMarks, RedactionPlan.Empty.WithRange(RedactionSearch.ToMarks(hits, "x", false, default, out _)).Marks.Count);
    }

    [Fact]
    public async Task ACompleteSearchSaysSoAndTheListOfWhatIsNeverSearchedIsStable()
    {
        await using var document = await OpenAsync("text-rich.pdf");
        var report = new RedactionSearchReport();

        _ = await FindAsync(document, "Pagewing", report: report);

        Assert.True(report.IsComplete);
        Assert.False(report.LimitReached);
        Assert.Empty(report.IncompletePages);
        Assert.Equal(8, RedactionSearch.NotSearched.Count);
        Assert.All(RedactionSearch.NotSearched, static line => Assert.EndsWith(".", line, StringComparison.Ordinal));
        foreach (var key in new[] { "/IRT", "popup", "/T)", "/Subj", "/TU", "field", "/RV", "/DV", "/AP", "render mode 3", "/AcroForm" })
        {
            Assert.Contains(RedactionSearch.NotSearched, line => line.Contains(key, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ThePatternLineOfWhatWasNotSearchedNamesEveryKnownGap()
    {
        // A run of the patterns must not read as complete: the line names what the built-in rules do not know.
        var line = Assert.Single(RedactionSearch.NotSearched, static l => l.StartsWith("Patterns", StringComparison.Ordinal));
        foreach (var gap in new[] { "line break", "page boundary", "numbers inside images", "IBANs of countries outside", "do not start with +, 0 or (", "more than 64 groups", "unusual characters", "other layouts" })
        {
            Assert.Contains(gap, line, StringComparison.Ordinal);
        }
    }

    // ---- what was not searched is reported ----

    [Fact]
    public async Task APageThatCannotBeReadIsReportedAndTheOthersAreStillSearched()
    {
        using var folder = new TempFolder();
        var pdf = new VerifierPdf(PageWith("Zephyr one"), PageWith("Zephyr two"), PageWith("Zephyr three"));
        await using var document = await OpenBytesAsync(folder, pdf.Build());
        using var source = new PageTextSource(
            document,
            (page, priority) => page == 1 ? Task.FromException<PageText>(new InvalidDataException("damaged")) : document.GetPageTextAsync(page, priority, Ct));
        var report = new RedactionSearchReport();

        var hits = await FindAsync(document, "Zephyr", texts: source, report: report);

        Assert.Equal([0, 2], hits.Select(static h => h.Page));
        Assert.Equal([1], report.IncompletePages);
        Assert.False(report.IsComplete);
    }

    [Fact]
    public async Task AnAnnotationTextBeyondTheSearchedLengthIsReportedAndItsStartIsSearched()
    {
        using var folder = new TempFolder();
        var pdf = new VerifierPdf(PageWith("Plain"), PageWith("Plain"));
        var big = pdf.Add($"<< /Type /Annot /Subtype /Text /Rect [10 20 30 40] /Contents (Zephyr {new string('a', RedactionSearch.MaxSourceTextLength)}) /F 4 >>");
        pdf.PageEntriesByPage = [$"/Annots [{big} 0 R]", string.Empty];
        await using var document = await OpenBytesAsync(folder, pdf.Build());
        var report = new RedactionSearchReport();

        var hits = await FindAsync(document, "Zephyr", report: report);

        Assert.Equal(RedactionHitSource.Annotation, Assert.Single(hits).Source);
        Assert.Equal([0], report.IncompletePages);
    }

    [Fact]
    public async Task TheSharedAnnotationLimitReportsEveryLaterPageThatHasAnnotations()
    {
        // 10,000 links on page one use up the limit for the whole document; the comment on page two is never listed.
        using var folder = new TempFolder();
        var pdf = new VerifierPdf(PageWith("one"), PageWith("two"), PageWith("three"));
        var links = Enumerable.Range(0, PdfDocument.MaxAnnotations).Select(static i => $"<< /Type /Annot /Subtype /Link /Rect [{i % 200} 5 {(i % 200) + 5} 10] /Border [0 0 0] >>").Select(pdf.Add).ToList();
        var note = pdf.Add($"<< /Type /Annot /Subtype /Text /Rect [10 20 30 40] /Contents {Utf16("Zephyr hides here")} /F 4 >>");
        pdf.PageEntriesByPage = [$"/Annots [{string.Join(' ', links.Select(static n => $"{n} 0 R"))}]", $"/Annots [{note} 0 R]", string.Empty];
        await using var document = await OpenBytesAsync(folder, pdf.Build());
        var report = new RedactionSearchReport();

        var hits = await FindAsync(document, "Zephyr", report: report);

        Assert.Empty(hits);
        Assert.Equal([1], report.IncompletePages);
    }

    [Fact]
    public async Task AnAnnotationWithoutARectIsReportedAsNotSearched()
    {
        using var folder = new TempFolder();
        var pdf = new VerifierPdf(PageWith("Plain"), PageWith("Plain"));
        var damaged = pdf.Add($"<< /Type /Annot /Subtype /Text /Contents {Utf16("Zephyr without a rect")} /F 4 >>");
        pdf.PageEntriesByPage = [string.Empty, $"/Annots [{damaged} 0 R]"];
        await using var document = await OpenBytesAsync(folder, pdf.Build());
        var report = new RedactionSearchReport();

        var hits = await FindAsync(document, "Zephyr", report: report);

        Assert.Empty(hits);
        Assert.Equal([1], report.IncompletePages);
    }

    [Fact]
    public async Task AFormFieldThatCouldNotBeReadAndAWidgetWithoutARectAreReported()
    {
        using var folder = new TempFolder();

        // A value longer than the limit: the widget is listed as unsupported with no value.
        var pdf = new VerifierPdf(PageWith("Plain"), PageWith("Plain"));
        var field = pdf.Add($"<< /Type /Annot /Subtype /Widget /FT /Tx /T (Long) /V ({new string('x', 100_001)}) /Rect [10 20 100 40] /F 4 >>");
        pdf.CatalogEntries = $"/AcroForm << /Fields [{field} 0 R] >>";
        pdf.PageEntriesByPage = [string.Empty, $"/Annots [{field} 0 R]"];
        await using (var document = await OpenBytesAsync(folder, pdf.Build()))
        {
            var report = new RedactionSearchReport();
            Assert.Empty(await FindAsync(document, "Zephyr", report: report));
            Assert.Equal([1], report.IncompletePages);
        }

        // A widget without a /Rect is not listed by the reader at all.
        var flat = new VerifierPdf(PageWith("Plain"));
        var widget = flat.Add("<< /Type /Annot /Subtype /Widget /FT /Tx /T (NoRect) /V (Zephyr) /F 4 >>");
        flat.CatalogEntries = $"/AcroForm << /Fields [{widget} 0 R] >>";
        flat.PageEntries = $"/Annots [{widget} 0 R]";
        await using (var document = await OpenBytesAsync(folder, flat.Build()))
        {
            var report = new RedactionSearchReport();
            Assert.Empty(await FindAsync(document, "Zephyr", report: report));
            Assert.Equal([0], report.IncompletePages);
        }

        // The hostile form has both (a field with too many options and a widget without a rect).
        await using var hostile = await OpenAsync("forms-hostile.pdf");
        var hostileReport = new RedactionSearchReport();
        _ = await FindAsync(hostile, "Zephyr", report: hostileReport);
        Assert.Equal([0], hostileReport.IncompletePages);

        // A readable form is complete, a signature or a push button alone does not make the page incomplete.
        await using var basic = await OpenAsync("forms-basic.pdf");
        var basicReport = new RedactionSearchReport();
        _ = await FindAsync(basic, "Fest", report: basicReport);
        Assert.True(basicReport.IsComplete);
        await using var signed = await OpenAsync("forms-signed.pdf");
        var signedReport = new RedactionSearchReport();
        _ = await FindAsync(signed, "Zephyr", report: signedReport);
        Assert.True(signedReport.IsComplete);
    }

    [Fact]
    public async Task TheSearchReadsTheFormsInTheBackgroundWithTheSameFieldsAsTheViewerDoes()
    {
        foreach (var name in new[] { "forms-basic.pdf", "forms-rotated.pdf", "forms-calc.pdf", "forms-hostile.pdf" })
        {
            await using var document = await OpenAsync(name);
            for (var page = 0; page < document.PageCount; page++)
            {
                var interactive = await document.GetFormFieldsAsync(page, Ct);
                var (background, leftOut) = await document.GetFormFieldsForSearchAsync(page, Ct);

                Assert.Equal(interactive.Select(static f => (f.Key, f.Bounds, f.State, f.Lock)), background.Select(static f => (f.Key, f.Bounds, f.State, f.Lock)));
                Assert.Equal(name == "forms-hostile.pdf", leftOut);
            }
        }

        // The listing of annotations says nothing more and nothing less than the public one.
        await using var annotated = await OpenAsync("annotations-foreign.pdf");
        for (var page = 0; page < annotated.PageCount; page++)
        {
            var (listed, incomplete) = await annotated.GetAnnotationsWithStatusAsync(page, Ct);
            Assert.Equal((await annotated.GetAnnotationsAsync(page, Ct)).Select(static a => (a.Index, a.Name, a.Contents)), listed.Select(static a => (a.Index, a.Name, a.Contents)));
            Assert.False(incomplete);
        }
    }

    // ---- whole words and "everywhere" ----

    [Fact]
    public async Task WholeWordNeverMakesAMarkEverywhere()
    {
        using var folder = new TempFolder();
        await using var document = await OpenBytesAsync(folder, new VerifierPdf(PageWith("Die Liste ist lang, es ist gut")).Build());
        var whole = new SearchOptions(WholeWord: true);

        var hits = await FindAsync(document, "ist", whole);
        Assert.Equal(2, hits.Count);

        var marks = RedactionSearch.ToMarks(hits, "ist", everywhere: true, whole, out _);
        Assert.Equal(2, marks.Count);
        Assert.All(marks, static m => Assert.False(m.Everywhere));
        Assert.False(RedactionSearch.ToMark(hits[0], "ist", everywhere: true, whole).Everywhere);

        // Without whole words the wish is kept.
        var plain = await FindAsync(document, "ist");
        Assert.True(RedactionSearch.ToMark(plain[0], "ist", everywhere: true, default).Everywhere);
        Assert.False(RedactionSearch.ToMark(plain[0], "ist", everywhere: false, default).Everywhere);
    }

    [Fact]
    public async Task TheCheckAcceptsAWholeWordRedactionOfATextWithListeButRefusesTheEverywhereWish()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "liste.pdf");
        File.WriteAllBytes(path, new VerifierPdf(PageWith("Die Liste ist lang, es ist gut")).Build());
        var whole = new SearchOptions(WholeWord: true);

        // As the search makes it: every whole-word hit is marked, nothing demands that "Liste" loses its letters.
        using (var session = new ViewerSession(engine))
        {
            await session.OpenAsync(path);
            var hits = await FindAsync(session.Document!, "ist", whole);
            var plan = RedactionPlan.Empty.WithRange(RedactionSearch.ToMarks(hits, "ist", everywhere: true, whole, out _));

            var result = await RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, Path.Combine(folder.Path, "out.pdf")), null, Ct);

            Assert.True(result.Check.IsClean, RedactionRefusalText.Of(result.Check));
            var text = string.Concat((await session.Document!.GetPageTextAsync(0, Ct)).Chars.Select(static c => c.Text));
            Assert.Contains("Liste", text, StringComparison.Ordinal);
            Assert.Contains("lang", text, StringComparison.Ordinal);
        }

        // The same hits made "everywhere" by hand would demand 0 occurrences of "ist" in "Liste" as well.
        using var other = new ViewerSession(engine);
        await other.OpenAsync(path);
        var found = await FindAsync(other.Document!, "ist", whole);
        var forced = RedactionPlan.Empty.WithRange(found.Select(static h => new RedactionMark(Guid.NewGuid(), RedactionKind.Hit, h.Page, [.. h.Areas], h.Text, Everywhere: true)));
        var refused = await Assert.ThrowsAsync<SaveFailureException>(() => RedactionApplier.ApplyAsync(other, engine, new RedactionApplyRequest(forced, Path.Combine(folder.Path, "forced.pdf")), null, Ct));
        Assert.Equal(SaveFailureReason.RedactionFailed, refused.Reason);
        Assert.False(File.Exists(Path.Combine(folder.Path, "forced.pdf")));
    }
}

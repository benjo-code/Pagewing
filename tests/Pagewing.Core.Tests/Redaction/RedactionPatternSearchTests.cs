using System.Text;
using Pagewing.Core.Pdf;
using Pagewing.Core.Redaction;
using Pagewing.Core.Saving;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Text;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Redaction;

/// <summary>"Search and redact" for patterns (milestone 14.1, task 8): the hits of the scanners in all sources, the marks, and the raw-bytes check of the form without separators.</summary>
public sealed class RedactionPatternSearchTests : IDisposable
{
    private const string GroupedIban = "DE89 3704 0044 0532 0130 00";
    private const string UngroupedIban = "DE89370400440532013000";

    private readonly PdfEngine engine = new();
    private readonly TempFolder folder = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IReadOnlySet<RedactionPatternKind> All = new HashSet<RedactionPatternKind>
    {
        RedactionPatternKind.Email,
        RedactionPatternKind.Iban,
        RedactionPatternKind.Phone,
        RedactionPatternKind.Card,
    };

    public void Dispose() => folder.Dispose();

    private static async Task<List<RedactionHit>> FindAsync(PdfDocument document, IReadOnlySet<RedactionPatternKind>? kinds = null, RedactionSearchReport? report = null, PageTextSource? texts = null)
    {
        var hits = new List<RedactionHit>();
        await foreach (var hit in RedactionPatternSearch.FindAsync(document, kinds ?? All, report, texts, Ct))
        {
            hits.Add(hit);
        }

        return hits;
    }

    private async Task<PdfDocument> OpenAsync(string name) => await engine.OpenAsync(TestData.Pdf(name), cancellationToken: Ct);

    private async Task<PdfDocument> OpenBytesAsync(byte[] bytes)
    {
        var path = Path.Combine(folder.Path, Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, bytes);
        return await engine.OpenAsync(path, cancellationToken: Ct);
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(folder.Path, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string Utf16(string text) => "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text)) + ">";

    private static string PageWith(string text) => $"BT /F1 12 Tf 20 150 Td ({text}) Tj ET";

    private static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);

    // ---- the fixture ----

    [Fact]
    public async Task FixtureYieldsTheExpectedHitsPerKindAndCertainty()
    {
        await using var document = await OpenAsync("redact-patterns.pdf");
        var report = new RedactionSearchReport();

        var hits = await FindAsync(document, report: report);

        var counts = hits.GroupBy(static h => (h.Pattern!.Value, h.Certainty!.Value)).ToDictionary(static g => g.Key, static g => g.Count());
        Assert.Equal(
            new Dictionary<(RedactionPatternKind, PatternCertainty), int>
            {
                [(RedactionPatternKind.Iban, PatternCertainty.ChecksumOk)] = 6,
                [(RedactionPatternKind.Iban, PatternCertainty.ChecksumFailed)] = 1,
                [(RedactionPatternKind.Card, PatternCertainty.ChecksumOk)] = 3,
                [(RedactionPatternKind.Card, PatternCertainty.ChecksumFailed)] = 1,
                [(RedactionPatternKind.Phone, PatternCertainty.ShapeOnly)] = 4,
                [(RedactionPatternKind.Email, PatternCertainty.ShapeOnly)] = 3,
            },
            counts);

        // The sources: the page, the note, the two text fields.
        Assert.Equal(15, hits.Count(static h => h.Source == RedactionHitSource.PageText));
        Assert.Equal(1, hits.Count(static h => h.Source == RedactionHitSource.Annotation));
        Assert.Equal(2, hits.Count(static h => h.Source == RedactionHitSource.FormField));
        var note = Assert.Single(hits, static h => h.Source == RedactionHitSource.Annotation);
        Assert.Equal(GroupedIban, note.Text);
        Assert.Equal(RedactionPatternKind.Iban, note.Pattern);
        Assert.Equal(
            ["form@example.org", "+49 711 7654321"],
            hits.Where(static h => h.Source == RedactionHitSource.FormField).Select(static h => h.Text));

        // The text of a page hit is the page text as it stands, with a space for a no-break space and for a gap between glyphs.
        var page = hits.Where(static h => h.Source == RedactionHitSource.PageText).Select(static h => h.Text).ToList();
        Assert.Contains(GroupedIban, page);
        Assert.Contains("GB82 WEST 1234 5698 7654 32", page);
        Assert.Contains("FR14 2004 1010 0505 0001 3M02 606", page);
        Assert.Contains("NL91ABNA0417164300", page);
        Assert.Contains("4111-1111-1111-1111", page);
        Assert.Contains("(0711) 123 45 67", page);
        Assert.Contains("a+b@sub.example.co.uk", page);
        Assert.DoesNotContain(page, static t => t.Contains("localhost", StringComparison.Ordinal) || t.Contains("2024", StringComparison.Ordinal) || t.Contains("148410", StringComparison.Ordinal) || t.Contains("12345678901234567890", StringComparison.Ordinal));

        Assert.All(hits, static h =>
        {
            Assert.True(h.Markable, h.NotMarkableReason);
            Assert.NotEmpty(h.Areas);
            Assert.True(h.Text.Length >= RedactionLimits.MinRawCheckLength);
        });
        Assert.True(report.IsComplete);

        // One kind at a time gives the same hits of that kind.
        var onlyEmails = await FindAsync(document, new HashSet<RedactionPatternKind> { RedactionPatternKind.Email });
        Assert.Equal(3, onlyEmails.Count);
        Assert.All(onlyEmails, static h => Assert.Equal(RedactionPatternKind.Email, h.Pattern));
    }

    [Fact]
    public async Task ChecksumFailedHitsAreFlagged()
    {
        await using var document = await OpenAsync("redact-patterns.pdf");

        var hits = await FindAsync(document);

        var failed = hits.Where(static h => h.Certainty == PatternCertainty.ChecksumFailed).ToList();
        Assert.Equal(["DE88 3704 0044 0532 0130 00", "1234 5678 9012 3456"], failed.Select(static h => h.Text).Order(StringComparer.Ordinal).Reverse());
        Assert.Equal(RedactionPatternKind.Iban, failed.Single(static h => h.Text.StartsWith("DE88", StringComparison.Ordinal)).Pattern);
        Assert.Equal(RedactionPatternKind.Card, failed.Single(static h => h.Text.StartsWith("1234", StringComparison.Ordinal)).Pattern);
        Assert.All(hits.Where(static h => h.Pattern is RedactionPatternKind.Email or RedactionPatternKind.Phone), static h => Assert.Equal(PatternCertainty.ShapeOnly, h.Certainty));
        Assert.All(hits.Where(static h => h.Certainty == PatternCertainty.ChecksumOk), static h => Assert.True(h.Pattern is RedactionPatternKind.Iban or RedactionPatternKind.Card));
    }

    [Fact]
    public async Task PatternHitsAreHitMarksWithoutEverywhere()
    {
        await using var document = await OpenAsync("redact-patterns.pdf");
        var hits = await FindAsync(document);

        var marks = RedactionSearch.ToMarks(hits, out var notMarkable);

        Assert.Equal(0, notMarkable);
        Assert.Equal(hits.Count, marks.Count);
        Assert.All(marks, static m => Assert.Equal(RedactionKind.Hit, m.Kind));
        Assert.All(marks, static m => Assert.False(m.Everywhere));
        Assert.Equal(hits.Select(static h => h.Pattern), marks.Select(static m => m.Pattern));
        Assert.Equal(hits.Select(static h => h.Text), marks.Select(static m => m.Term));

        // Asking for "everywhere" through the overload of the word search changes nothing for a pattern hit.
        var asked = RedactionSearch.ToMarks(hits, "query", everywhere: true, default, out _);
        Assert.All(asked, static m => Assert.False(m.Everywhere));
        Assert.All(asked, static m => Assert.NotNull(m.Pattern));

        // A hit of a term still has no pattern.
        var word = Assert.Single(await FindWordAsync(document, "form@example.org"));
        Assert.Null(word.Pattern);
        Assert.Null(word.Certainty);
        Assert.Null(RedactionSearch.ToMark(word, "form@example.org", everywhere: false, default).Pattern);

        // The overload without a query is for pattern hits only: a hit of a term is refused, nothing is made of the others.
        var refused = Assert.Throws<ArgumentException>(() => RedactionSearch.ToMarks([hits[0], word], out _));
        Assert.Equal("hits", refused.ParamName);
        Assert.Empty(RedactionSearch.ToMarks([], out var none));
        Assert.Equal(0, none);
        var unmarkable = hits[0] with { Markable = false, NotMarkableReason = "test" };
        Assert.Single(RedactionSearch.ToMarks([hits[1], unmarkable], out var skipped));
        Assert.Equal(1, skipped);
    }

    private static async Task<List<RedactionHit>> FindWordAsync(PdfDocument document, string term)
    {
        var hits = new List<RedactionHit>();
        await foreach (var hit in RedactionSearch.FindAsync(document, term, default, null, null, Ct))
        {
            hits.Add(hit);
        }

        return hits;
    }

    [Fact]
    public async Task SplitPatternIsNotFoundAndNamed()
    {
        await using var document = await OpenAsync("redact-patterns.pdf");
        var report = new RedactionSearchReport();

        var hits = await FindAsync(document, new HashSet<RedactionPatternKind> { RedactionPatternKind.Iban, RedactionPatternKind.Phone, RedactionPatternKind.Card }, report);

        // The IBAN of the last two lines is split across a line break: neither half is a hit.
        Assert.DoesNotContain(hits, static h => h.Text.Contains("Konto", StringComparison.Ordinal) || h.Text.Contains("split", StringComparison.Ordinal));
        Assert.Equal(7, hits.Count(static h => h.Pattern == RedactionPatternKind.Iban));

        // ... and the list of what is never searched says so, in every run.
        Assert.Contains(RedactionSearch.NotSearched, static line => line.Contains("split across a line break", StringComparison.Ordinal));
        Assert.True(report.IsComplete);
    }

    [Fact]
    public async Task APatternDoesNotCrossALineBreakEvenWhenBothHalvesLookLikeOne()
    {
        // 16 characters of an IBAN on the first line and the rest on the second: the first line alone is a (wrong) IBAN shape of
        // a known country, the whole is not found as one.
        var pdf = new VerifierPdf("BT /F1 12 Tf 20 150 Td (DE89 3704 0044 0532) Tj ET BT /F1 12 Tf 20 130 Td (0130 00) Tj ET");
        await using var document = await OpenBytesAsync(pdf.Build());

        var hits = await FindAsync(document, new HashSet<RedactionPatternKind> { RedactionPatternKind.Iban });

        var hit = Assert.Single(hits);
        Assert.Equal("DE89 3704 0044 0532", hit.Text);
        Assert.Equal(PatternCertainty.ChecksumFailed, hit.Certainty);
    }

    [Fact]
    public async Task NoKindsFindsNothing()
    {
        await using var document = await OpenAsync("redact-patterns.pdf");
        var report = new RedactionSearchReport();

        Assert.Empty(await FindAsync(document, new HashSet<RedactionPatternKind>(), report));
        Assert.True(report.IsComplete);
    }

    [Fact]
    public async Task HitLimitStopsAndReports()
    {
        // 130 pages with 10 lines of 10 addresses are 13,000 places; the search stops at the number of marks a plan can hold and says so.
        var line = string.Join(' ', Enumerable.Repeat("a@b.de", 10));
        var content = string.Concat(Enumerable.Range(0, 10).Select(i => $"BT /F1 6 Tf 5 {190 - (i * 7)} Td ({line}) Tj ET\n"));
        var pdf = new VerifierPdf(Enumerable.Repeat(content, 130).ToArray());
        await using var document = await OpenBytesAsync(pdf.Build());
        var report = new RedactionSearchReport();

        var hits = await FindAsync(document, new HashSet<RedactionPatternKind> { RedactionPatternKind.Email }, report);

        Assert.Equal(RedactionSearch.MaxHits, hits.Count);
        Assert.True(report.LimitReached);
        Assert.False(report.IsComplete);
        Assert.Equal(RedactionLimits.MaxMarks, RedactionPlan.Empty.WithRange(RedactionSearch.ToMarks(hits, out _)).Marks.Count);
    }

    [Fact]
    public async Task APageThatCannotBeReadIsReportedAndNeverTakenForNoHits()
    {
        var pdf = new VerifierPdf(PageWith("a@b.de"), PageWith("c@d.de"), PageWith("e@f.de"));
        await using var document = await OpenBytesAsync(pdf.Build());
        using var source = new PageTextSource(
            document,
            (page, priority) => page == 1 ? Task.FromException<PageText>(new InvalidDataException("damaged")) : document.GetPageTextAsync(page, priority, Ct));
        var report = new RedactionSearchReport();

        var hits = await FindAsync(document, report: report, texts: source);

        Assert.Equal([0, 2], hits.Select(static h => h.Page));
        Assert.Equal([1], report.IncompletePages);
        Assert.False(report.IsComplete);
    }

    [Fact]
    public async Task AnAnnotationTextBeyondTheSearchedLengthIsReportedAndItsStartIsSearched()
    {
        var pdf = new VerifierPdf(PageWith("Plain"));
        var big = pdf.Add($"<< /Type /Annot /Subtype /Text /Rect [10 20 30 40] /Contents (x@y.de {new string('a', RedactionSearch.MaxSourceTextLength)}) /F 4 >>");
        pdf.PageEntries = $"/Annots [{big} 0 R]";
        await using var document = await OpenBytesAsync(pdf.Build());
        var report = new RedactionSearchReport();

        var hits = await FindAsync(document, report: report);

        var hit = Assert.Single(hits);
        Assert.Equal(RedactionHitSource.Annotation, hit.Source);
        Assert.Equal("x@y.de", hit.Text);
        Assert.Equal([0], report.IncompletePages);
    }

    [Fact]
    public async Task AnAddressNextToAnEmojiInACommentIsFoundWithItsOwnText()
    {
        // The scanner counts UTF-16 units, the snippet counts code points: the emoji must not shift the hit.
        var pdf = new VerifierPdf(PageWith("Plain"));
        var note = pdf.Add($"<< /Type /Annot /Subtype /Text /Rect [10 20 30 40] /Contents {Utf16("\U0001F600 mail \U0001F600max@example.com\U0001F600 or 0711 1234567")} /F 4 >>");
        pdf.PageEntries = $"/Annots [{note} 0 R]";
        await using var document = await OpenBytesAsync(pdf.Build());

        var hits = await FindAsync(document);

        Assert.Equal(["max@example.com", "0711 1234567"], hits.Select(static h => h.Text));
        Assert.All(hits, static h => Assert.Equal(RedactionHitSource.Annotation, h.Source));
        Assert.Equal("max@example.com", hits[0].Snippet.Match);
    }

    [Fact]
    public void NotSearchedNamesSplitPatterns()
    {
        var line = Assert.Single(RedactionSearch.NotSearched, static l => l.StartsWith("Patterns", StringComparison.Ordinal));

        Assert.Contains("split across a line break or a page boundary", line, StringComparison.Ordinal);
        Assert.Contains("images", line, StringComparison.Ordinal);
        Assert.EndsWith(".", line, StringComparison.Ordinal);
        Assert.Equal(8, RedactionSearch.NotSearched.Count);
    }

    // ---- the form without separators ----

    [Fact]
    public void TheFormWithoutSeparatorsDropsSpacesDashesSlashesDotsAndBrackets()
    {
        Assert.Equal(UngroupedIban, RemovedText.RawFormOf(GroupedIban));
        Assert.Equal(UngroupedIban, RemovedText.RawFormOf("DE89 3704 0044 0532 0130-00"));
        Assert.Equal("07111234567", RemovedText.RawFormOf("0711/1234567"));
        Assert.Equal("+4971112345", RemovedText.RawFormOf("+49 (711) 123.45"));
        Assert.Equal("4111111111111111", RemovedText.RawFormOf("4111-1111-1111-1111"));

        // Nothing to remove, or too little left: no extra form.
        Assert.Null(RemovedText.RawFormOf(UngroupedIban));
        Assert.Null(RemovedText.RawFormOf("1-2 3"));
        Assert.Null(RemovedText.RawFormOf("1 2-3"));
        Assert.Equal("1234", RemovedText.RawFormOf("12 3-4"));
    }

    [Fact]
    public async Task TheExpectationCarriesTheFormWithoutSeparatorsOnlyForGroupedNumbers()
    {
        await using var document = await OpenAsync("redact-patterns.pdf");
        var area = new PageRect(10, 10, 20, 20);
        var plan = RedactionPlan.Empty
            .With(new RedactionMark(Guid.NewGuid(), RedactionKind.Hit, 0, [area], GroupedIban, false, null, RedactionPatternKind.Iban))
            .With(new RedactionMark(Guid.NewGuid(), RedactionKind.Hit, 0, [area], "4111-1111-1111-1111", false, null, RedactionPatternKind.Card))
            .With(new RedactionMark(Guid.NewGuid(), RedactionKind.Hit, 0, [area], "(0711) 123 45 67", false, null, RedactionPatternKind.Phone))
            .With(new RedactionMark(Guid.NewGuid(), RedactionKind.Hit, 0, [area], "max.mustermann@example.com", false, null, RedactionPatternKind.Email))
            .With(new RedactionMark(Guid.NewGuid(), RedactionKind.Hit, 0, [area], "NL91ABNA0417164300", false, null, RedactionPatternKind.Iban))
            .With(new RedactionMark(Guid.NewGuid(), RedactionKind.Text, 0, [area], "DE88 3704 0044 0532 0130 00", false));

        var expectation = await RedactionExecutor.ExpectationAsync(plan, document, Ct);

        string? RawFormOf(string text) => expectation.Removed.Single(r => r.Text == text).RawForm;
        Assert.Equal(UngroupedIban, RawFormOf(GroupedIban));
        Assert.Equal("4111111111111111", RawFormOf("4111-1111-1111-1111"));
        Assert.Equal("07111234567", RawFormOf("(0711) 123 45 67"));
        Assert.Null(RawFormOf("max.mustermann@example.com"));
        Assert.Null(RawFormOf("NL91ABNA0417164300"));
        Assert.Null(RawFormOf("DE88 3704 0044 0532 0130 00"));
    }

    private async Task<RedactionCheck> VerifyAsync(string path, RedactionExpectation expectation)
    {
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        await using var stream = File.OpenRead(path);
        return await RedactionVerifier.VerifyAsync(document, stream, expectation, Ct);
    }

    private static RedactionExpectation Expect(string text, int before, bool withRawForm)
    {
        var plan = RedactionPlan.Empty.With(new RedactionMark(Guid.NewGuid(), RedactionKind.Hit, 0, [new PageRect(250, 150, 290, 190)], text, false, null, RedactionPatternKind.Iban));
        return new RedactionExpectation(plan, [new RemovedText(0, text, Everywhere: false, before, withRawForm ? RemovedText.RawFormOf(text) : null)], plan.Fill);
    }

    [Fact]
    public async Task GroupedIbanLeftInRawBytesWithoutSpacesFailsTheRawCheck()
    {
        // The result shows nothing of the IBAN, but the information dictionary still holds it without the spaces of its groups.
        var pdf = new VerifierPdf(PageWith("Public line"));
        var info = pdf.Add($"<< /Subject ({UngroupedIban}) >>");
        pdf.TrailerEntries = $"/Info {info} 0 R";
        var path = Write("leak.pdf", pdf.Build());

        // Without the form the check is blind to it: the grouped text is not in the bytes ...
        var blind = await VerifyAsync(path, Expect(GroupedIban, before: 1, withRawForm: false));
        Assert.DoesNotContain(blind.Findings, static f => f.Kind == RedactionFindingKind.RawBytesStillPresent);

        // ... with the form it fails, and says it in words that repeat nothing of the number.
        var check = await VerifyAsync(path, Expect(GroupedIban, before: 1, withRawForm: true));
        var finding = Assert.Single(check.Findings, static f => f.Kind == RedactionFindingKind.RawBytesStillPresent);
        Assert.Null(finding.Page);
        Assert.Equal([0], finding.RelatedPages);
        Assert.DoesNotContain("DE89", finding.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(check.Findings, static f => f.Kind == RedactionFindingKind.TextStillPresent);

        // The same IBAN in a stream and in a hex string of an attachment fails as well.
        var attached = new VerifierPdf(PageWith("Public line"));
        var attachment = attached.AddStream("/Type /EmbeddedFile", Latin1("Konto " + UngroupedIban + "\n"));
        attached.CatalogEntries = $"/Names << /EmbeddedFiles << /Names [(a.txt) << /EF << /F {attachment} 0 R >> >>] >> >>";
        var attachedCheck = await VerifyAsync(Write("attached.pdf", attached.Build()), Expect(GroupedIban, before: 1, withRawForm: true));
        Assert.Contains(attachedCheck.Findings, static f => f.Kind == RedactionFindingKind.RawBytesStillPresent);

        // A clean file passes the same expectation.
        var clean = await VerifyAsync(Write("clean.pdf", new VerifierPdf(PageWith("Public line")).Build()), Expect(GroupedIban, before: 1, withRawForm: true));
        Assert.True(clean.IsClean, string.Join(" | ", clean.Findings.Select(static f => f.Detail)));
    }

    /// <summary>One IBAN whose groups are set apart by the spacing of the glyphs only: no space in the content stream, spaces in the page text.</summary>
    private const string SpacedByGlyphs = "BT /F1 12 Tf 20 150 Td [(DE89) -600 (3704) -600 (0044) -600 (0532) -600 (0130) -600 (00)] TJ ET";

    [Fact]
    public async Task UnfoundSecondOccurrenceDoesNotFailTheCheck()
    {
        // Two copies stood in the original; the scanner found one. The other one, which the scanner could not recognise, stays: the
        // page shows it with spaces between its groups while the content stream holds the groups without any.
        var path = Write("second.pdf", new VerifierPdf(SpacedByGlyphs).Build());
        await using (var document = await engine.OpenAsync(path, cancellationToken: Ct))
        {
            var text = await document.GetPageTextAsync(0, Ct);
            Assert.Single(TextMatcher.Find(text.Chars, GroupedIban, new SearchOptions(MatchCase: true)));
        }

        using (var stream = File.OpenRead(path))
        {
            Assert.Equal(1, RawContentScanner.Count(stream, UngroupedIban, RawScanLimits.Default, out _, Ct));
        }

        var check = await VerifyAsync(path, Expect(GroupedIban, before: 2, withRawForm: true));

        Assert.True(check.IsClean, string.Join(" | ", check.Findings.Select(static f => f.Detail)));
        Assert.Contains(check.NotChecked, static l => l.Contains("cannot tell a leaked copy", StringComparison.Ordinal));

        // A leaked copy on top of the legitimate one is still caught: two in the bytes, one in the page text.
        var leaking = new VerifierPdf(SpacedByGlyphs);
        var info = leaking.Add($"<< /Subject ({UngroupedIban}) >>");
        leaking.TrailerEntries = $"/Info {info} 0 R";
        var leak = await VerifyAsync(Write("second-and-leak.pdf", leaking.Build()), Expect(GroupedIban, before: 2, withRawForm: true));
        Assert.Contains(leak.Findings, static f => f.Kind == RedactionFindingKind.RawBytesStillPresent);

        // A copy that the page shows without any space is a legitimate occurrence of the form itself.
        var plain = await VerifyAsync(Write("plain.pdf", new VerifierPdf(PageWith("Old " + UngroupedIban)).Build()), Expect(GroupedIban, before: 1, withRawForm: true));
        Assert.DoesNotContain(plain.Findings, static f => f.Kind == RedactionFindingKind.RawBytesStillPresent);
    }

    [Fact]
    public async Task ALegitimateGroupedCopyDoesNotHideALeakedUngroupedCopy()
    {
        // The page keeps one unmarked copy with real spaces; the bytes hold it once in the content stream. A second, ungrouped copy sits
        // in the information dictionary, outside the page text. One legitimate place may cover one raw copy, not one in each writing.
        var leaking = new VerifierPdf(PageWith("Konto " + GroupedIban + " Ende"));
        var info = leaking.Add($"<< /Subject ({UngroupedIban}) >>");
        leaking.TrailerEntries = $"/Info {info} 0 R";
        var leakPath = Write("legit-and-ungrouped-leak.pdf", leaking.Build());
        await using (var document = await engine.OpenAsync(leakPath, cancellationToken: Ct))
        {
            var text = await document.GetPageTextAsync(0, Ct);
            Assert.Single(TextMatcher.Find(text.Chars, GroupedIban, new SearchOptions(MatchCase: true)));
            Assert.Empty(TextMatcher.Find(text.Chars, UngroupedIban, new SearchOptions(MatchCase: true)));
        }

        using (var stream = File.OpenRead(leakPath))
        {
            Assert.Equal(1, RawContentScanner.Count(stream, GroupedIban, RawScanLimits.Default, out _, Ct));
            Assert.Equal(1, RawContentScanner.Count(stream, UngroupedIban, RawScanLimits.Default, out _, Ct));
        }

        var leak = await VerifyAsync(leakPath, Expect(GroupedIban, before: 2, withRawForm: true));
        var finding = Assert.Single(leak.Findings, static f => f.Kind == RedactionFindingKind.RawBytesStillPresent);
        Assert.DoesNotContain("DE89", finding.Detail, StringComparison.Ordinal);
        Assert.Contains("2 time(s)", finding.Detail, StringComparison.Ordinal);
        Assert.Contains("accounts for 1", finding.Detail, StringComparison.Ordinal);

        // The sibling with exactly the allowed total passes: the legitimate copy alone, and two legitimate copies in the two writings.
        var one = await VerifyAsync(Write("legit-one.pdf", new VerifierPdf(PageWith("Konto " + GroupedIban + " Ende")).Build()), Expect(GroupedIban, before: 2, withRawForm: true));
        Assert.DoesNotContain(one.Findings, static f => f.Kind == RedactionFindingKind.RawBytesStillPresent);
        var both = await VerifyAsync(
            Write("legit-both.pdf", new VerifierPdf(PageWith("Konto " + GroupedIban + " Ende"), PageWith("Old " + UngroupedIban)).Build()),
            Expect(GroupedIban, before: 3, withRawForm: true));
        Assert.DoesNotContain(both.Findings, static f => f.Kind == RedactionFindingKind.RawBytesStillPresent);

        // Without the form the same file is the old rule for a word: one raw copy against one in the page text.
        var word = await VerifyAsync(leakPath, Expect(GroupedIban, before: 2, withRawForm: false));
        Assert.DoesNotContain(word.Findings, static f => f.Kind == RedactionFindingKind.RawBytesStillPresent);
    }

    [Fact]
    public async Task EverywhereKeepsZeroForTheFormWithoutSeparators()
    {
        var path = Write("everywhere.pdf", new VerifierPdf(SpacedByGlyphs).Build());
        var plan = RedactionPlan.Empty.With(new RedactionMark(Guid.NewGuid(), RedactionKind.Hit, 0, [new PageRect(250, 150, 290, 190)], GroupedIban, true));
        var expectation = new RedactionExpectation(plan, [new RemovedText(0, GroupedIban, Everywhere: true, 1, RemovedText.RawFormOf(GroupedIban))], plan.Fill);

        var check = await VerifyAsync(path, expectation);

        // "Everywhere" allows nothing: not in the page text, and not in the bytes either, in either writing.
        Assert.Single(check.Findings, static f => f.Kind == RedactionFindingKind.TextStillPresent);
        Assert.Single(check.Findings, static f => f.Kind == RedactionFindingKind.RawBytesStillPresent);
    }

    // ---- end to end ----

    [Fact]
    public async Task AFoundIbanIsRedactedWriteVerifiedAndAbsentInEveryWritingForm()
    {
        var content = $"BT /F1 12 Tf 20 150 Td (Konto: {GroupedIban} Ende) Tj ET BT /F1 12 Tf 20 120 Td (Konto: {UngroupedIban} Ende) Tj ET BT /F1 12 Tf 20 90 Td (Public line stays) Tj ET {SpacedByGlyphs.Replace("150", "60", StringComparison.Ordinal)}";
        var path = Write("source.pdf", new VerifierPdf(content).Build());
        await using var shown = await engine.OpenAsync(path, cancellationToken: Ct);
        var hits = await FindAsync(shown, new HashSet<RedactionPatternKind> { RedactionPatternKind.Iban });
        Assert.Equal(3, hits.Count);
        Assert.All(hits, static h => Assert.Equal(PatternCertainty.ChecksumOk, h.Certainty));
        var plan = RedactionPlan.Empty.WithRange(RedactionSearch.ToMarks(hits, out _));
        var target = Path.Combine(folder.Path, "redacted.pdf");

        var result = await RedactionApplier.WriteAsync(engine, shown, path, null, FileStamp.Of(path), new RedactionApplyRequest(plan, target, ReplaceExisting: false), null, Ct);

        await using var reopened = result.Saved.Reopened;
        Assert.True(result.Check.IsClean, string.Join(" | ", result.Check.Findings.Select(static f => f.Detail)));
        var text = string.Concat((await reopened.GetPageTextAsync(0, Ct)).Chars.Select(static c => c.Text));
        Assert.Contains("Public line stays", text, StringComparison.Ordinal);
        Assert.DoesNotContain("0532", text, StringComparison.Ordinal);
        var bytes = await File.ReadAllBytesAsync(target, Ct);
        Assert.False(RawPdf.ContainsAnywhere(bytes, UngroupedIban));
        Assert.False(RawPdf.ContainsAnywhere(bytes, GroupedIban));
        Assert.False(RawPdf.ContainsAnywhere(bytes, "37040044"));
        Assert.False(RawPdf.ContainsAnywhere(bytes, "05320130"));
    }
}

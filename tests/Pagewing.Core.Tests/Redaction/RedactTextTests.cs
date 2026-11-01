using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Pagewing.Core.Pdf;
using Pagewing.Core.Redaction;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Tests.Pdfium;
using Pagewing.Core.Text;
using Pagewing.Pdfium;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Redaction;

/// <summary>
/// Removing text and annotations under rectangles (milestone 14, task 4) on copies of the redaction fixtures. The tests read the
/// result back with PDFium's exact doubles (the probe of the spike) because the page text of the core rounds to 1/16 pt.
/// </summary>
public sealed class RedactTextTests
{
    private const string Term = "Zephyrstraße 17";

    private static readonly int[] FallbackLines = [3, 4];
    private static readonly int[] TextStateLines = [6, 7, 8];
    private static readonly int[] MixedLines = [3, 4, 6];

    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Copy(TempFolder folder, string name)
    {
        var path = Path.Combine(folder.Path, name);
        File.Copy(TestData.Pdf(name), path);
        return path;
    }

    /// <summary>The characters PDFium reports for a page of a file (not generated), at full precision.</summary>
    private static async Task<ProbeChar[]> ReadExactAsync(string path, int pageIndex)
    {
        var thread = PdfiumThread.Shared;
        return await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, path, null);
                using var page = PdfiumRedaction.OpenPage(document, pageIndex);
                var textPage = page.LoadTextPage();
                try
                {
                    return RedactionProbe.ReadChars(textPage).Where(static c => !c.Generated).ToArray();
                }
                finally
                {
                    page.CloseTextPage(textPage);
                }
            },
            cancellationToken: Ct);
    }

    private static bool White(ProbeChar c) => c.Unicode <= 0x20;

    /// <summary>The characters in groups of one text object each, in the order of the page.</summary>
    private static List<List<ProbeChar>> ByObject(IEnumerable<ProbeChar> chars)
    {
        var groups = new List<List<ProbeChar>>();
        nint last = 0;
        foreach (var c in chars)
        {
            if (groups.Count == 0 || c.TextObject != last)
            {
                groups.Add([]);
                last = c.TextObject;
            }

            groups[^1].Add(c);
        }

        return groups;
    }

    private static ProbeRect Union(IEnumerable<ProbeChar> chars) => chars.Select(static c => c.Box).Aggregate(static (a, b) => a.Union(b));

    /// <summary>The rectangle in points from the top left of a page of this height that covers a user space rectangle.</summary>
    private static PageRect Display(ProbeRect user, double pageHeight, double grow = 0) =>
        new(user.Left - grow, pageHeight - user.Top - grow, user.Right + grow, pageHeight - user.Bottom + grow);

    private static ProbeRect UserOf(PageRect display, double pageHeight) => new(display.Left, pageHeight - display.Bottom, display.Right, pageHeight - display.Top);

    private static bool Touches(ProbeRect a, ProbeRect b) =>
        a.Left <= b.Right + 1e-6 && b.Left <= a.Right + 1e-6 && a.Bottom <= b.Top + 1e-6 && b.Bottom <= a.Top + 1e-6;

    /// <summary>Every (start) index in the list where the term's letters (without the space) occur one after the other, among the non-white characters.</summary>
    private static List<int> Find(List<ProbeChar> letters, string term)
    {
        var wanted = term.Where(static c => c > ' ').ToArray();
        var found = new List<int>();
        for (var i = 0; i + wanted.Length <= letters.Count; i++)
        {
            var match = true;
            for (var k = 0; k < wanted.Length && match; k++)
            {
                match = letters[i + k].Unicode == wanted[k];
            }

            if (match)
            {
                found.Add(i);
            }
        }

        return found;
    }

    /// <summary>Saves a document as a full rewrite and returns the path of the copy.</summary>
    private static async Task<string> SaveAsync(PdfDocument document, TempFolder folder, string name)
    {
        var path = Path.Combine(folder.Path, name);
        await using (var stream = File.Create(path))
        {
            await document.SaveRewriteAsync(stream, Ct);
        }

        return path;
    }

    private static void AssertSurvivorsStayInPlace(IReadOnlyList<ProbeChar> original, IReadOnlyList<ProbeChar> after, IReadOnlyList<ProbeRect> userAreas, out int removed)
    {
        var survivors = original.Where(c => !White(c) && !userAreas.Any(a => Touches(a, c.Box))).ToList();
        var now = after.Where(static c => !White(c)).ToList();
        removed = original.Count(c => !White(c)) - survivors.Count;
        Assert.Equal(survivors.Count, now.Count);
        for (var i = 0; i < survivors.Count; i++)
        {
            Assert.Equal(survivors[i].Unicode, now[i].Unicode);
            Assert.True(Math.Abs(survivors[i].OriginX - now[i].OriginX) <= 0.01 && Math.Abs(survivors[i].OriginY - now[i].OriginY) <= 0.01, $"Character {i} moved from ({survivors[i].OriginX}, {survivors[i].OriginY}) to ({now[i].OriginX}, {now[i].OriginY}).");
            Assert.True(
                Math.Abs(survivors[i].Box.Left - now[i].Box.Left) <= 0.01 && Math.Abs(survivors[i].Box.Right - now[i].Box.Right) <= 0.01
                && Math.Abs(survivors[i].Box.Bottom - now[i].Box.Bottom) <= 0.01 && Math.Abs(survivors[i].Box.Top - now[i].Box.Top) <= 0.01,
                $"The box of character {i} changed.");
        }
    }

    [Fact]
    public async Task CharsUnderTheAreaAreGoneAndTheRestStaysInPlace()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-everywhere.pdf");
        var original = await ReadExactAsync(path, 0);
        var letters = original.Where(static c => !White(c)).ToList();
        var first = Find(letters, Term)[0];
        var termBox = Union(letters.Skip(first).Take(Term.Count(static c => c > ' ')));
        var area = Display(termBox, 400);

        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [area], RedactionFill.Black, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(1, impact.TextObjectsSplit);
        Assert.Contains(Term, impact.RemovedText);
        var after = await ReadExactAsync(await SaveAsync(document, folder, "out.pdf"), 0);

        AssertSurvivorsStayInPlace(original, after, [termBox], out var removed);
        Assert.Equal(Term.Count(static c => c > ' '), removed);
        Assert.Equal(Term.Length, impact.CharsRemoved);

        // The visible line lost the term; the other two copies of it (in the form, invisible) are outside the area and stay.
        Assert.Equal(2, Find(after.Where(static c => !White(c)).ToList(), Term).Count);
    }

    [Fact]
    public async Task ACharTouchedOnlyByItsEdgeIsRemoved()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-everywhere.pdf");
        var original = await ReadExactAsync(path, 0);
        var letters = original.Where(static c => !White(c)).ToList();
        var first = Find(letters, Term)[0];
        var term = letters.Skip(first).Take(Term.Count(static c => c > ' ')).ToList();
        var last = term[^1];

        // The area ends exactly where the last character's box begins: they only touch.
        var rest = Union(term.Take(term.Count - 1));
        var area = new ProbeRect(rest.Left, rest.Bottom, last.Box.Left, rest.Top);
        Assert.True(area.Right > area.Left);
        Assert.True(Touches(area, last.Box));

        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        await document.RedactPageAsync(0, [Display(area, 400)], RedactionFill.Black, Ct);
        var after = await ReadExactAsync(await SaveAsync(document, folder, "out.pdf"), 0);

        // Nothing of the term is left, not even the character that was only touched.
        AssertSurvivorsStayInPlace(original, after, [area], out var removed);
        Assert.Equal(term.Count, removed);
        Assert.Equal(2, Find(after.Where(static c => !White(c)).ToList(), Term).Count);
    }

    [Fact]
    public async Task InvisibleTextUnderTheAreaIsRemoved()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-everywhere.pdf");
        var original = await ReadExactAsync(path, 0);
        var letters = original.Where(static c => !White(c)).ToList();
        var occurrences = Find(letters, Term);
        Assert.Equal(3, occurrences.Count);

        // The third copy is the invisible one (render mode 3) over the scan.
        var invisible = Union(letters.Skip(occurrences[2]).Take(Term.Count(static c => c > ' ')));
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [Display(invisible, 400)], RedactionFill.Black, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(1, impact.TextObjectsRemovedWhole);
        Assert.Contains(Term, impact.RemovedText);
        var after = await ReadExactAsync(await SaveAsync(document, folder, "out.pdf"), 0);

        AssertSurvivorsStayInPlace(original, after, [invisible], out _);
        Assert.Equal(2, Find(after.Where(static c => !White(c)).ToList(), Term).Count);
        Assert.DoesNotContain("Scan above carries invisible text.", string.Concat(after.Select(static c => (char)c.Unicode)).Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RotatedPageAndCropBoxRemoveTheRightChars()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-rotated.pdf");
        PageText text;
        await using (var reader = await engine.OpenAsync(path, cancellationToken: Ct))
        {
            text = await reader.GetPageTextAsync(0, Ct);
        }

        var letters = text.Chars.Where(static c => !c.IsGenerated && c.CodePoint > 0x20).ToList();
        var wanted = Term.Where(static c => c > ' ').Select(static c => (int)c).ToArray();
        var at = Enumerable.Range(0, letters.Count - wanted.Length + 1).First(i => Enumerable.Range(0, wanted.Length).All(k => letters[i + k].CodePoint == wanted[k]));
        var box = letters.Skip(at).Take(wanted.Length).Select(static c => c.Box).Aggregate(static (a, b) => a.Union(b)).Inflate(0.5);
        var originalLetters = string.Concat(letters.Select(static c => c.Text));

        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [box], RedactionFill.Black, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(1, impact.TextObjectsSplit);
        var saved = await SaveAsync(document, folder, "out.pdf");
        await using var reopened = await engine.OpenAsync(saved, cancellationToken: Ct);
        var after = string.Concat((await reopened.GetPageTextAsync(0, Ct)).Chars.Where(static c => !c.IsGenerated && c.CodePoint > 0x20).Select(static c => c.Text));

        // Exactly the term is gone: the line above and below, which lie where a mirrored rectangle would be, are untouched.
        var term = string.Concat(Term.Where(static c => c > ' '));
        Assert.Equal(originalLetters.Remove(originalLetters.IndexOf(term, StringComparison.Ordinal), term.Length), after);
        Assert.Contains("Topline", after, StringComparison.Ordinal);
        Assert.Contains("Bottomline", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TextObjectWithoutReadableResultIsRemovedWholeAndListed()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-fonts.pdf");
        var original = await ReadExactAsync(path, 0);
        var groups = ByObject(original);
        Assert.Equal(9, groups.Count);

        // Line 3 (CIDToGIDMap stream) and line 4 (no ToUnicode): SetText reports success, nothing readable comes out.
        var areas = FallbackLines.Select(line => Display(Union(groups[line].Skip(7).Take(3)), 420)).ToList();
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, areas, RedactionFill.Black, Ct);

        Assert.Null(impact.Refusal);
        Assert.Equal(0, impact.TextObjectsSplit);
        Assert.Equal(2, impact.TextObjectsRemovedWhole);
        Assert.Equal(2, impact.Fallbacks.Count(f => f.Kind == RedactionFallbackKind.TextObjectRemovedWhole && f.Page == 0 && f.Reason.Length > 0));
        Assert.All(impact.Fallbacks, f => Assert.Contains("could not", f.Reason, StringComparison.Ordinal));

        // The whole lines are gone; the lines around them are not touched.
        var after = await ReadExactAsync(await SaveAsync(document, folder, "out.pdf"), 0);
        var remaining = ByObject(after);
        Assert.Equal(7, remaining.Count);
        Assert.Equal(groups[2].Count, remaining[2].Count);
    }

    [Fact]
    public async Task SplitTextKeepsKerningAndSpacing()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-fonts.pdf");
        var original = await ReadExactAsync(path, 0);
        var groups = ByObject(original);

        // Line 6 has TJ adjustments, line 7 Tc, Tw and Tz, line 8 is turned by 30 degrees.
        var userAreas = TextStateLines.Select(line => Union(groups[line].Skip(7).Take(3))).ToList();
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [.. userAreas.Select(a => Display(a, 420))], RedactionFill.Black, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(3, impact.TextObjectsSplit);
        Assert.Empty(impact.Fallbacks);
        var saved = await SaveAsync(document, folder, "out.pdf");

        // Outside the rectangles the three lines are pixel for pixel what they were.
        var before = await RedactionRender.RenderAsync(engine, path, 2);
        var rendered = await RedactionRender.RenderAsync(engine, saved, 2);
        // A character counts as soon as its box touches a rectangle, and then all of it goes (a turned line has neighbours that do): compare outside those too.
        var gone = userAreas.Concat(original.Where(c => userAreas.Any(a => Touches(a, c.Box))).Select(static c => c.Box)).ToList();
        var (different, compared, _) = RedactionProbe.CompareOutside(before, rendered, 2, gone, 1.5, within: new ProbeRect(0, 40, 420, 228));
        Assert.True(compared > 10_000);
        Assert.Equal(0, different);
    }

    [Fact]
    public async Task EverySplitObjectIsReadBackAndTheRestIsUntouched()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-fonts.pdf");
        var original = await ReadExactAsync(path, 0);
        var groups = ByObject(original);

        // One rectangle over three characters of every line whose font can be rewritten.
        var lines = new[] { 0, 1, 2, 5, 6, 7, 8 };
        var userAreas = lines.Select(line => Union(groups[line].Skip(7).Take(3))).ToList();
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [.. userAreas.Select(a => Display(a, 420))], RedactionFill.Black, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(lines.Length, impact.TextObjectsSplit);
        Assert.Empty(impact.Fallbacks);
        var after = await ReadExactAsync(await SaveAsync(document, folder, "out.pdf"), 0);

        // The characters that stay have their Unicode, origin (0.02 pt) and box (0.05 pt) exactly as before. Lines 3 and 4 (fonts that
        // PDFium cannot write) were not touched by an area and are not part of this.
        static bool OnTheFragileLines(ProbeChar c) => Math.Abs(c.OriginY - 300) < 1 || Math.Abs(c.OriginY - 270) < 1;
        var before = lines.SelectMany(line => groups[line]).Where(c => !White(c) && !userAreas.Any(a => Touches(a, c.Box))).ToList();
        var now = after.Where(c => !White(c) && !OnTheFragileLines(c)).ToList();
        Assert.Equal(before.Count, now.Count);
        for (var i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Unicode, now[i].Unicode);
            Assert.True(Math.Abs(before[i].OriginX - now[i].OriginX) < 0.02 && Math.Abs(before[i].OriginY - now[i].OriginY) < 0.02, $"Character {i} moved.");
            Assert.True(Math.Abs(before[i].Box.Left - now[i].Box.Left) < 0.05 && Math.Abs(before[i].Box.Right - now[i].Box.Right) < 0.05, $"Character {i} changed its width.");
            Assert.True(Math.Abs(before[i].Box.Bottom - now[i].Box.Bottom) < 0.05 && Math.Abs(before[i].Box.Top - now[i].Box.Top) < 0.05, $"Character {i} changed its height.");
        }
    }

    [Fact]
    public async Task TextInsideAFormObjectRemovesTheFormObjectWholeUntilFormsAreTakenApart()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-everywhere.pdf");
        var original = await ReadExactAsync(path, 0);
        var letters = original.Where(static c => !White(c)).ToList();
        var occurrences = Find(letters, Term);
        var inForm = Union(letters.Skip(occurrences[1]).Take(Term.Count(static c => c > ' ')));

        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [Display(inForm, 400)], RedactionFill.Black, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(1, impact.FormObjectsTouched);
        Assert.Single(impact.Fallbacks, f => f.Kind == RedactionFallbackKind.FormObjectRemovedWhole);
        Assert.Contains(Term, impact.RemovedText);
        var saved = await SaveAsync(document, folder, "out.pdf");

        // The form's text is gone from page 1 (nothing else of the page changed) and page 2, which uses the same XObject, still shows it.
        var after = await ReadExactAsync(saved, 0);
        Assert.Equal(2, Find(after.Where(static c => !White(c)).ToList(), Term).Count);
        var pageTwo = await ReadExactAsync(saved, 1);
        Assert.Single(Find(pageTwo.Where(static c => !White(c)).ToList(), Term));
    }

    [Fact]
    public async Task AnnotationsInTheAreaAreDeletedWithPopupAndReplies()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "annotations-foreign.pdf");
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var before = await document.GetAnnotationsAsync(0, Ct);

        // The highlight [38 246 250 264] has a popup [260 200 380 260] and a reply [280 246 298 264], both outside the rectangle.
        var area = new PageRect(38, 300 - 264, 250, 300 - 246);
        var impact = await document.RedactPageAsync(0, [area], RedactionFill.Black, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(3, impact.AnnotationsRemoved);
        var saved = await SaveAsync(document, folder, "out.pdf");

        await using var reopened = await engine.OpenAsync(saved, cancellationToken: Ct);
        var after = await reopened.GetAnnotationsAsync(0, Ct);
        Assert.Equal(before.Count - 1, after.Count);
        Assert.DoesNotContain(after, a => a.Name == "hl-0001");
        Assert.Contains(after, a => a.Name == "ink-0001");

        // Highlight, popup and reply are gone from the file: no trace of what they said.
        var bytes = await File.ReadAllBytesAsync(saved, Ct);
        Assert.Equal(0, RawPdf.Count(bytes, "Check this sentence"));
        Assert.Equal(0, RawPdf.Count(bytes, "Agreed"));
        Assert.Equal(1, RawPdf.Count(bytes, "Free text note"));
    }

    [Fact]
    public async Task AnnotationsInTheAreaAreDeletedWithTheirReplyInTheRedactionFixture()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-everywhere.pdf");
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);

        // The note [250 340 268 358]; its reply [270 340 288 358] points to it with /IRT.
        var impact = await document.RedactPageAsync(0, [new PageRect(250, 400 - 358, 268, 400 - 340)], RedactionFill.Black, Ct);
        Assert.Equal(2, impact.AnnotationsRemoved);
        var bytes = await File.ReadAllBytesAsync(await SaveAsync(document, folder, "out.pdf"), Ct);
        Assert.Equal(0, RawPdf.Count(bytes, "(Re: "));
        Assert.Equal(0, RawPdf.Count(bytes, "/Contents (Zephyr"));
    }

    [Fact]
    public async Task AnalyzeChangesNothing()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-everywhere.pdf");
        var original = await ReadExactAsync(path, 0);
        var letters = original.Where(static c => !White(c)).ToList();
        var termBox = Union(letters.Skip(Find(letters, Term)[0]).Take(Term.Count(static c => c > ' ')));
        PageRect[] areas = [Display(termBox, 400), new PageRect(250, 400 - 358, 268, 400 - 340)];

        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var bytesBefore = await File.ReadAllBytesAsync(await SaveAsync(document, folder, "before.pdf"), Ct);
        var dry = await document.AnalyzeRedactionAsync(0, areas, Ct);
        var bytesAfter = await File.ReadAllBytesAsync(await SaveAsync(document, folder, "after.pdf"), Ct);
        Assert.Equal(WithoutId(bytesBefore), WithoutId(bytesAfter));

        // Applying does what the dry run said.
        var applied = await document.RedactPageAsync(0, areas, RedactionFill.Black, Ct);
        Assert.Equal(dry, applied, new ImpactComparer());
        Assert.Equal(1, dry.TextObjectsSplit);
        Assert.Equal(2, dry.AnnotationsRemoved);
    }

    [Fact]
    public async Task AnalyzeSaysTheSameAsApplyingWhenTextCannotBeRewritten()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-fonts.pdf");
        var groups = ByObject(await ReadExactAsync(path, 0));
        PageRect[] areas = [.. MixedLines.Select(line => Display(Union(groups[line].Skip(7).Take(3)), 420))];

        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var dry = await document.AnalyzeRedactionAsync(0, areas, Ct);
        var applied = await document.RedactPageAsync(0, areas, RedactionFill.Black, Ct);
        Assert.Equal(2, dry.Fallbacks.Count);
        Assert.Equal(1, dry.TextObjectsSplit);
        Assert.Equal(dry, applied, new ImpactComparer());
    }

    [Fact]
    public async Task AreaOutsideThePageIsClippedAndTouchesNothing()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-everywhere.pdf");
        var original = await ReadExactAsync(path, 0);
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);

        // Fully outside (and one partly outside, which only has its part on the page): no effect except a clipped area.
        var outside = await document.RedactPageAsync(0, [new PageRect(1000, 1000, 1100, 1100), new PageRect(-90, -90, -10, -10), new PageRect(500, 5, 900, 25)], RedactionFill.Black, Ct);
        Assert.Equal(0, outside.CharsRemoved);
        Assert.Equal(0, outside.AnnotationsRemoved);
        Assert.Empty(outside.Fallbacks);
        Assert.Null(outside.Refusal);
        var untouched = await ReadExactAsync(await SaveAsync(document, folder, "outside.pdf"), 0);
        AssertSurvivorsStayInPlace(original, untouched, [], out _);

        // Partly outside: the part on the page counts, the rest is clipped.
        var letters = original.Where(static c => !White(c)).ToList();
        var termBox = Union(letters.Skip(Find(letters, Term)[0]).Take(Term.Count(static c => c > ' ')));
        var area = Display(termBox, 400);
        await using var second = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await second.RedactPageAsync(0, [new PageRect(area.Left, area.Top, 5000, 5000)], RedactionFill.Black, Ct);
        Assert.Null(impact.Refusal);
        Assert.True(impact.CharsRemoved >= Term.Length);
    }

    [Fact]
    public async Task HostileAreasAreRefusedWithAClearException()
    {
        using var folder = new TempFolder();
        await using var document = await engine.OpenAsync(Copy(folder, "redact-everywhere.pdf"), cancellationToken: Ct);
        foreach (var bad in new[]
        {
            new PageRect(double.NaN, 0, 10, 10),
            new PageRect(0, 0, double.PositiveInfinity, 10),
            new PageRect(10, 0, 0, 10),
            new PageRect(0, 10, 10, 0),
            new PageRect(5, 5, 5, 20),
        })
        {
            var error = await Assert.ThrowsAsync<RedactionException>(() => document.AnalyzeRedactionAsync(0, [bad], Ct));
            Assert.Equal(RedactionError.InvalidArea, error.Error);
        }

        var many = Enumerable.Repeat(new PageRect(0, 0, 1, 1), PdfDocument.MaxRedactionAreasPerPage + 1).ToList();
        var limit = await Assert.ThrowsAsync<RedactionException>(() => document.RedactPageAsync(0, many, RedactionFill.Black, Ct));
        Assert.Equal(RedactionError.LimitExceeded, limit.Error);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.AnalyzeRedactionAsync(5, [new PageRect(0, 0, 1, 1)], Ct));
    }

    [Fact]
    public async Task TenThousandAreasFinishInTime()
    {
        using var folder = new TempFolder();
        await using var document = await engine.OpenAsync(Copy(folder, "redact-everywhere.pdf"), cancellationToken: Ct);
        var areas = Enumerable.Range(0, PdfDocument.MaxRedactionAreasPerPage).Select(static i => new PageRect(i % 290, ((i / 290) % 390) + 0.25, (i % 290) + 0.5, ((i / 290) % 390) + 0.5)).ToList();
        var clock = Stopwatch.StartNew();
        var impact = await document.AnalyzeRedactionAsync(0, areas, Ct);
        Assert.Null(impact.Refusal);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"Took {clock.Elapsed}.");
    }

    [Fact]
    public async Task FillIsExactlyTheAreaAndOpaque()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-everywhere.pdf");
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);

        // Page 2 (300 x 400) shows only a line at the top, so the area lies over blank paper. At scale 4 the edges are whole pixels.
        var area = new PageRect(100, 200, 180.25, 260);
        var impact = await document.RedactPageAsync(1, [area], RedactionFill.Black, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(0, impact.CharsRemoved);
        var saved = await SaveAsync(document, folder, "out.pdf");
        var before = await RedactionRender.RenderAsync(engine, path, 4, 1);
        var after = await RedactionRender.RenderAsync(engine, saved, 4, 1);

        const int left = 400;
        const int right = 721; // 180.25 pt * 4, exclusive
        const int top = 800;
        const int bottom = 1040;
        for (var y = top - 1; y <= bottom; y++)
        {
            for (var x = left - 1; x <= right; x++)
            {
                var inside = x >= left && x < right && y >= top && y < bottom;
                var a = Pixel(after, x, y);
                if (inside)
                {
                    Assert.Equal((0, 0, 0), a);
                }
                else
                {
                    Assert.Equal(Pixel(before, x, y), a);
                }
            }
        }
    }

    [Fact]
    public async Task WhiteFillWorks()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-covered.pdf");
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);

        // The black bar of the fixture [15 112 275 136] is 200 high: the white area (scale 2) lies inside it.
        var area = new PageRect(40, 66, 100, 86);
        await document.RedactPageAsync(0, [area], RedactionFill.White, Ct);
        var saved = await SaveAsync(document, folder, "out.pdf");
        var before = await RedactionRender.RenderAsync(engine, path, 2);
        var after = await RedactionRender.RenderAsync(engine, saved, 2);
        Assert.Equal((0, 0, 0), Pixel(before, 100, 140));
        Assert.Equal((255, 255, 255), Pixel(after, 100, 140));
        Assert.Equal((255, 255, 255), Pixel(after, 80, 132));
        Assert.Equal((0, 0, 0), Pixel(after, 78, 140));
    }

    /// <summary>The file without its trailer <c>/ID</c>, which PDFium makes new at every save.</summary>
    private static string WithoutId(byte[] file) => Regex.Replace(Encoding.Latin1.GetString(file), @"/ID\[<[0-9A-Fa-f]*><[0-9A-Fa-f]*>\]", "/ID[]");

    /// <summary>A page with 20,000 text objects "ab" in 200 rows of 100 columns (spaced 6 pt, the rows 3.8 pt).</summary>
    private static string GridOfTextObjects()
    {
        var content = new StringBuilder();
        for (var row = 0; row < 200; row++)
        {
            for (var column = 0; column < 100; column++)
            {
                content.Append(CultureInfo.InvariantCulture, $"BT /F1 4 Tf {6 + (column * 6)} {780 - (row * 3.8):0.0} Td (ab) Tj ET\n");
            }
        }

        return content.ToString();
    }

    private static (int R, int G, int B) Pixel(RenderedImage image, int x, int y)
    {
        var offset = (y * image.Stride) + (x * 4);
        var span = image.Pixels.Span;
        return (span[offset + 2], span[offset + 1], span[offset]);
    }

    [Fact]
    public async Task TwentyThousandTextObjectsOnOnePageFinishInTimeAndLeaveTheThreadFree()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "many-text.pdf");
        await File.WriteAllBytesAsync(path, HostilePdf.Build(GridOfTextObjects()), Ct);
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);

        // The left half of the page: 10,000 objects go, and the 200 of the next column are cut through their "a" (the "b" stays).
        var area = new PageRect(0, 0, 307.2, 792);
        var thread = PdfiumThread.Shared;
        var clock = Stopwatch.StartNew();
        var running = document.RedactPageAsync(0, [area], RedactionFill.Black, Ct);
        var longestWait = TimeSpan.Zero;
        var probes = 0;
        while (!running.IsCompleted)
        {
            var asked = Stopwatch.StartNew();
            _ = await thread.InvokeAsync(static () => 0, PdfiumPriority.Interactive, Ct);
            longestWait = asked.Elapsed > longestWait ? asked.Elapsed : longestWait;
            probes++;
            await Task.Delay(5, Ct);
        }

        var impact = await running;
        Assert.Null(impact.Refusal);
        Assert.Equal(10_000, impact.TextObjectsRemovedWhole);
        Assert.Equal(200, impact.TextObjectsSplit);
        Assert.Empty(impact.Fallbacks);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(90), $"Took {clock.Elapsed}.");
        Assert.True(probes >= 5, $"Only {probes} probes while the job ran for {clock.Elapsed}.");
        Assert.True(longestWait < TimeSpan.FromSeconds(2), $"Rendering waited {longestWait} for the thread (job took {clock.Elapsed}).");
    }

    [Fact]
    public async Task TenThousandAreasOnTwentyThousandTextObjectsLeaveTheThreadFree()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "many-text.pdf");
        await File.WriteAllBytesAsync(path, HostilePdf.Build(GridOfTextObjects()), Ct);
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var areas = new List<PageRect>();
        for (var i = 0; i < 10_000; i++)
        {
            var row = i / 100;
            var column = i % 100;
            var x = 6 + (column * 6);
            var y = 780 - (row * 3.8);
            areas.Add(new PageRect(x + 0.3, 792 - y - 3, x + 1.8, 792 - y + 1));
        }

        var thread = PdfiumThread.Shared;
        var clock = Stopwatch.StartNew();
        var running = document.RedactPageAsync(0, areas, RedactionFill.Black, Ct);
        var longestWait = TimeSpan.Zero;
        while (!running.IsCompleted)
        {
            var asked = Stopwatch.StartNew();
            _ = await thread.InvokeAsync(static () => 0, PdfiumPriority.Interactive, Ct);
            longestWait = asked.Elapsed > longestWait ? asked.Elapsed : longestWait;
            await Task.Delay(5, Ct);
        }

        var impact = await running;
        Assert.Null(impact.Refusal);
        Assert.Equal(10_000, impact.TextObjectsSplit);
        Assert.Equal(10_000, impact.CharsRemoved);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), $"Took {clock.Elapsed}.");
        Assert.True(longestWait < TimeSpan.FromSeconds(2), $"Rendering waited {longestWait} for the thread (job took {clock.Elapsed}).");
    }

    [Fact]
    public async Task CancelingStopsTheJobAndLeavesTheDocumentUsable()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "many-text.pdf");
        await File.WriteAllBytesAsync(path, HostilePdf.Build(GridOfTextObjects()), Ct);
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        using var source = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var running = document.AnalyzeRedactionAsync(0, [new PageRect(0, 0, 612, 792)], source.Token);
        await source.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        // The scratch copy is gone and the document is as it was: a new run gives the full result.
        var impact = await document.AnalyzeRedactionAsync(0, [new PageRect(0, 0, 612, 792)], Ct);
        Assert.Equal(20_000, impact.TextObjectsRemovedWhole);
    }

    [Fact]
    public async Task PageWithMoreObjectsThanTheCapIsRefusedWithReason()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "many-paths.pdf");
        var content = new StringBuilder("BT /F1 12 Tf 20 700 Td (Zephyr) Tj ET\n");
        for (var i = 0; i <= PdfiumRedaction.MaxObjects; i++)
        {
            content.Append("0 0 1 1 re f\n");
        }

        await File.WriteAllBytesAsync(path, HostilePdf.Build(content.ToString()), Ct);
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        foreach (var impact in new[]
        {
            await document.AnalyzeRedactionAsync(0, [new PageRect(0, 0, 600, 100)], Ct),
            await document.RedactPageAsync(0, [new PageRect(0, 0, 600, 100)], RedactionFill.Black, Ct),
        })
        {
            Assert.NotNull(impact.Refusal);
            Assert.Equal(RedactionRefusalKind.PageNotRedactable, impact.Refusal.Kind);
            Assert.Equal([0], impact.Refusal.Pages);
            Assert.Contains((PdfiumRedaction.MaxObjects + 2).ToString(CultureInfo.InvariantCulture), impact.Refusal.Reason, StringComparison.Ordinal);
            Assert.Equal(0, impact.CharsRemoved);
        }

        // A refused page was not changed.
        var saved = await ReadExactAsync(await SaveAsync(document, folder, "out.pdf"), 0);
        Assert.Equal(6, saved.Count(static c => !White(c)));
    }

    [Fact]
    public async Task FormObjectWithHugeChildCountOrTextIsBoundedAndSaysSo()
    {
        using var folder = new TempFolder();

        // A form with more children than the limit: its text cannot be located, so the form is removed as a whole, and says why.
        var many = new StringBuilder("BT /F1 12 Tf 20 700 Td (Zephyr in the big form) Tj ET\n");
        for (var i = 0; i <= PdfiumRedaction.MaxFormChildren; i++)
        {
            many.Append("0 0 1 1 re f\n");
        }

        var bigPath = Path.Combine(folder.Path, "big-form.pdf");
        await File.WriteAllBytesAsync(bigPath, HostilePdf.Build("q /Fm1 Do Q\nBT /F1 12 Tf 20 100 Td (Stays outside) Tj ET\n", many.ToString()), Ct);
        await using (var document = await engine.OpenAsync(bigPath, cancellationToken: Ct))
        {
            var impact = await document.RedactPageAsync(0, [new PageRect(15, 792 - 715, 200, 792 - 695)], RedactionFill.Black, Ct);
            Assert.Null(impact.Refusal);
            var fallback = Assert.Single(impact.Fallbacks);
            Assert.Equal(RedactionFallbackKind.FormObjectRemovedWhole, fallback.Kind);
            Assert.Contains("too large", fallback.Reason, StringComparison.Ordinal);
            Assert.Contains(PdfiumRedaction.MaxFormChildren.ToString(CultureInfo.InvariantCulture), fallback.Reason, StringComparison.Ordinal);
        }

        // A text object longer than the limit inside a form: removed with the form, and the reason says that its text is not listed.
        var longPath = Path.Combine(folder.Path, "long-text-form.pdf");
        // (PDFium cuts one string operand at 32,767 characters, so the text is an array of strings in one text object.)
        var piece = new string('A', 30_000);
        await File.WriteAllBytesAsync(longPath, HostilePdf.Build("q /Fm1 Do Q\n", $"BT /F1 2 Tf 20 700 Td [({piece})({piece})({piece})({piece})] TJ ET\n"), Ct);
        await using (var document = await engine.OpenAsync(longPath, cancellationToken: Ct))
        {
            var impact = await document.RedactPageAsync(0, [new PageRect(15, 792 - 715, 25, 792 - 695)], RedactionFill.Black, Ct);
            Assert.Null(impact.Refusal);
            var fallback = Assert.Single(impact.Fallbacks);
            Assert.Equal(RedactionFallbackKind.FormObjectRemovedWhole, fallback.Kind);
            Assert.True(fallback.Reason.Contains("longer than the limit", StringComparison.Ordinal), $"{fallback.Reason} / removed texts: {string.Join(",", impact.RemovedText.Select(t => t.Length))} chars {impact.CharsRemoved}");
            Assert.DoesNotContain(impact.RemovedText, text => text.Length > PdfiumRedaction.MaxTextLength);
        }
    }

    [Fact]
    public void TheLimitsAgainstHostilePagesArePinned()
    {
        Assert.Equal(100_000, PdfiumRedaction.MaxObjects);
        Assert.Equal(500_000, PdfiumRedaction.MaxChars);
        Assert.Equal(20_000, PdfiumRedaction.MaxFormChildren);
        Assert.Equal(16, PdfiumRedaction.MaxFormDepth);
        Assert.Equal(100_000, PdfiumRedaction.MaxTextLength);
        Assert.Equal(10_000, PdfiumRedaction.MaxAreas);
        Assert.Equal(PdfiumRedaction.MaxAreas, PdfDocument.MaxRedactionAreasPerPage);
        Assert.Equal(3, PdfiumRedactionRun.MaxProbes);
    }

    /// <summary>Compares the numbers and lists of two impacts.</summary>
    private sealed class ImpactComparer : IEqualityComparer<PageRedactionImpact>
    {
        public bool Equals(PageRedactionImpact? x, PageRedactionImpact? y) =>
            x is not null && y is not null
            && x.Page == y.Page && x.CharsRemoved == y.CharsRemoved && x.TextObjectsSplit == y.TextObjectsSplit
            && x.TextObjectsRemovedWhole == y.TextObjectsRemovedWhole && x.FormObjectsTouched == y.FormObjectsTouched
            && x.AnnotationsRemoved == y.AnnotationsRemoved
            && x.Fallbacks.SequenceEqual(y.Fallbacks) && x.RemovedText.SequenceEqual(y.RemovedText)
            && Equals(x.Refusal?.Reason, y.Refusal?.Reason);

        public int GetHashCode(PageRedactionImpact obj) => obj.CharsRemoved;
    }
}

/// <summary>Builds small PDF files for the tests of the limits: one page with a content stream, Helvetica as /F1 and one form XObject as /Fm1.</summary>
internal static class HostilePdf
{
    public static byte[] Build(string content, string? form = null)
    {
        var builder = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();

        void Add(string body)
        {
            offsets.Add(builder.Length);
            builder.Append(CultureInfo.InvariantCulture, $"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }

        static string Stream(string dictionary, string data) =>
            $"<< {dictionary} /Length {data.Length} >>\nstream\n{data}\nendstream";

        Add("<< /Type /Catalog /Pages 2 0 R >>");
        Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> /XObject << /Fm1 6 0 R >> >> >>");
        Add(Stream(string.Empty, content));
        Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        Add(Stream("/Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >>", form ?? string.Empty));

        var xref = builder.Length;
        builder.Append(CultureInfo.InvariantCulture, $"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        builder.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(builder.ToString());
    }
}

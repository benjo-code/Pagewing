using System.Globalization;
using Pagewing.Core.Pdf;
using Pagewing.Pdfium;
using Pagewing.Pdfium.Native;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.TextEdit;

/// <summary>
/// The spike of milestone 16 (Docs/DECISIONS.md, "Text bearbeiten: Spike (M16)"): how a run of text is read and replaced with PDFium and what
/// the result survives. The assertions are the behaviour found with PDFium 157.0.8100 and guard against an update that changes it; none of them
/// is an expectation to loosen. Set <c>PAGEWING_SPIKE_OUT</c> to a folder to keep the sample files there (<c>text-edit-*-edited.pdf</c>) for a
/// look in other viewers.
/// </summary>
public sealed class TextEditSpikeTests
{
    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<T> OnPageAsync<T>(string file, int pageIndex, Func<PdfiumDocument, nint, nint, T> work)
    {
        var thread = PdfiumThread.Shared;
        return thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, TestData.Pdf(file), null);
                using var page = document.LoadPage(pageIndex);
                using var text = page.LoadTextPage();
                return work(document, page.Access(), text.Access());
            },
            cancellationToken: Ct);
    }

    private static async Task<byte[]> EditAsync(string file, int pageIndex, string oldText, Action<PdfiumDocument, nint, nint> edit)
    {
        var thread = PdfiumThread.Shared;
        return await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, TestData.Pdf(file), null);
                using var page = document.LoadPage(pageIndex);
                using var text = page.LoadTextPage();
                var handle = page.Access();
                var item = TextEditProbe.ReadTexts(handle, text.Access()).Single(t => t.Text.TrimEnd() == oldText).Object;
                edit(document, handle, item);
                Assert.NotEqual(0, Fpdf.FPDFPage_GenerateContent(handle));
                using var stream = new MemoryStream();
                document.SaveIncremental(stream);
                return stream.ToArray();
            },
            cancellationToken: Ct);
    }

    private static string Save(byte[] bytes, string name)
    {
        var path = Path.Combine(Path.GetTempPath(), "pw-spike-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, bytes);
        var output = Environment.GetEnvironmentVariable("PAGEWING_SPIKE_OUT");
        if (!string.IsNullOrEmpty(output))
        {
            Directory.CreateDirectory(output);
            File.WriteAllBytes(Path.Combine(output, name), bytes);
        }

        return path;
    }

    private static Task<T> OnSavedAsync<T>(byte[] bytes, int pageIndex, Func<PdfiumDocument, nint, nint, T> work)
    {
        var thread = PdfiumThread.Shared;
        var path = Save(bytes, "scratch.pdf");
        return thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, path, null);
                using var page = document.LoadPage(pageIndex);
                using var text = page.LoadTextPage();
                return work(document, page.Access(), text.Access());
            },
            cancellationToken: Ct);
    }

    private static string XsOf(ObjectChars chars) => string.Join(' ', chars.X.Select(static x => x.ToString("F2", CultureInfo.InvariantCulture)));

    [Fact]
    public async Task RunsAreMergedAcrossTJShifts()
    {
        // One text object for a TJ array with kerning shifts and one for a line with character and word spacing; a change of font, size or colour
        // inside a text object makes PDFium cut it, so each part is a run of its own.
        var texts = await OnPageAsync("text-edit-styles.pdf", 0, static (_, page, textPage) => TextEditProbe.ReadTexts(page, textPage).Select(static t => t.Text).ToArray());

        Assert.Equal(["Total: ", "1,250.00 ", " EUR", "Pagewing", "Mixed ", "fonts here"], texts);
    }

    [Fact]
    public async Task FontKindIsRecognisedForSubsetStandardAndFull()
    {
        var subset = await OnPageAsync("text-edit-subset.pdf", 0, static (_, page, textPage) => TextEditProbe.ReadTexts(page, textPage));
        var standard = await OnPageAsync("text-edit-standard.pdf", 0, static (_, page, textPage) => TextEditProbe.ReadTexts(page, textPage));
        var full = await OnPageAsync("text-edit-full.pdf", 0, static (_, page, textPage) => TextEditProbe.ReadTexts(page, textPage));
        var locked = await OnPageAsync("text-edit-locked.pdf", 0, static (_, page, textPage) => TextEditProbe.ReadTexts(page, textPage));
        var type3 = await OnPageAsync("text-edit-locked.pdf", 2, static (_, page, textPage) => TextEditProbe.ReadTexts(page, textPage));

        Assert.All(subset, static t => Assert.True(t.Embedded));
        Assert.All(full, static t => Assert.True(t.Embedded));
        Assert.All(standard, static t => Assert.False(t.Embedded));
        Assert.Equal(["Helvetica", "Times-Roman"], standard.Select(static t => t.Font));

        // The subset prefix is not a reliable sign of a subset: PDFium keeps "AAAAAA+" for the Type0 font and drops it for the simple TrueType font.
        Assert.Equal(["AAAAAA+Geist", "Geist"], subset.Select(static t => t.Font));

        // A Type 3 font has no base font name; that is how it is told apart.
        Assert.Equal(string.Empty, type3.Single(static t => t.Text == "ABBA").Font);
        Assert.All(locked.Where(static t => t.Text != "Vertical"), static t => Assert.NotEqual(string.Empty, t.Font));
    }

    [Fact]
    public async Task ASetTextWithAMissingGlyphIsDetectedByReadBack()
    {
        var type0 = await OnPageAsync(
            "text-edit-subset.pdf",
            0,
            static (document, page, textPage) =>
            {
                var font = Fpdf.FPDFTextObj_GetFont(TextEditProbe.ReadTexts(page, textPage)[0].Object);
                return (Present: SpikeEditor.ReadBack(document.Access(), page, font, 14, "Invoice 1,250"), Missing: SpikeEditor.ReadBack(document.Access(), page, font, 14, "zk9é"));
            });
        Assert.Equal("Invoice 1,250", type0.Present);

        // Not in the ToUnicode map of the Type0 subset: PDFium writes the code 0, which reads back as U+0000 for each character.
        Assert.Equal("\0\0\0\0", type0.Missing);

        var standard = await OnPageAsync(
            "text-edit-standard.pdf",
            0,
            static (document, page, textPage) =>
            {
                var font = Fpdf.FPDFTextObj_GetFont(TextEditProbe.ReadTexts(page, textPage)[0].Object);
                return SpikeEditor.ReadBack(document.Access(), page, font, 14, "zä€ł→");
            });

        // A character outside WinAnsi becomes another character (U+00FF), never the same one: the read-back differs.
        Assert.Equal("zä€ÿÿ", standard);
    }

    [Fact]
    public async Task ASimpleSubsetFontHidesAMissingGlyphFromTheReadBack()
    {
        // The subset of "Kontoauszug März" has no outline for b, f and ß, yet PDFium writes the codes and reads them back: only the characters the
        // font already draws on the page can be trusted for an embedded font (the fallback of Spec 4, which is therefore the rule).
        var read = await OnPageAsync(
            "text-edit-subset.pdf",
            0,
            static (document, page, textPage) =>
            {
                var font = Fpdf.FPDFTextObj_GetFont(TextEditProbe.ReadTexts(page, textPage)[1].Object);
                return SpikeEditor.ReadBack(document.Access(), page, font, 14, "bfß");
            });

        Assert.Equal("bfß", read);
    }

    [Theory]
    [InlineData("Invoice total: 1,205 EUR")]
    [InlineData("Invoice total: 1,250,000 EUR")]
    [InlineData("Invoice 1 EUR")]
    public async Task UnchangedCharactersKeepTheirPositions(string newText)
    {
        const string OldText = "Invoice total: 1,250 EUR";
        var before = await OnPageAsync("text-edit-subset.pdf", 0, static (_, page, textPage) => SpikeEditor.CharsOf(page, TextEditProbe.ReadTexts(page, textPage)[0].Object));
        var bytes = await EditAsync("text-edit-subset.pdf", 0, OldText, (document, page, item) => SpikeEditor.Replace(document.Access(), page, item, newText));
        var after = await OnSavedAsync(bytes, 0, static (_, page, textPage) => SpikeEditor.CharsOf(page, TextEditProbe.ReadTexts(page, textPage)[0].Object));

        var prefix = 0;
        while (prefix < Math.Min(OldText.Length, newText.Length) && OldText[prefix] == newText[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < Math.Min(OldText.Length, newText.Length) - prefix && OldText[^(suffix + 1)] == newText[^(suffix + 1)])
        {
            suffix++;
        }

        Assert.Equal(newText, new string(after.Unicode.Select(static u => (char)u).ToArray()));
        for (var i = 0; i < prefix; i++)
        {
            Assert.Equal(before.X[i], after.X[i], 0.02);
        }

        // The suffix keeps its own spacing and moves as a whole.
        var shift = after.X[newText.Length - suffix] - before.X[OldText.Length - suffix];
        for (var i = 0; i < suffix; i++)
        {
            Assert.Equal(before.X[OldText.Length - suffix + i] + shift, after.X[newText.Length - suffix + i], 0.05);
        }

        Assert.Equal(newText.Length == OldText.Length, Math.Abs(shift) < 1.5);
    }

    [Fact]
    public async Task InPlaceReplacementLosesTheCharacterSpacingOfTheTextStateAfterSaving()
    {
        // The text state of the object (Tc here) stays on the object, but PDFium writes the page content without it: after saving, the line next to
        // the edited one that was not touched is laid out naturally. The replacement therefore checks the result on a copy of the page and refuses a
        // page it would change.
        var before = await OnPageAsync("text-edit-styles.pdf", 1, static (_, page, textPage) => SpikeEditor.CharsOf(page, TextEditProbe.ReadTexts(page, textPage).Single(static t => t.Text == "Tracked two").Object));
        var bytes = await EditAsync("text-edit-styles.pdf", 1, "Tracked one", (document, page, item) => SpikeEditor.Replace(document.Access(), page, item, "Tracked ones"));
        var after = await OnSavedAsync(bytes, 1, static (_, page, textPage) => SpikeEditor.CharsOf(page, TextEditProbe.ReadTexts(page, textPage).Single(static t => t.Text == "Tracked two").Object));

        Assert.Equal(before.X.Length, after.X.Length);
        Assert.Equal(before.X[1] - 1.0, after.X[1], 0.05);
    }

    [Fact]
    public async Task ReplacedTextIsInTheNewRevisionOnlyOnceInExtractionAndContentBytes()
    {
        var bytes = await EditAsync("text-edit-standard.pdf", 0, "Quarterly report 2026", (document, page, item) => SpikeEditor.Replace(document.Access(), page, item, "Quarterly report 2027"));
        var extracted = await OnSavedAsync(bytes, 0, static (_, _, textPage) => TextEditProbe.TextOfPage(textPage));

        Assert.Equal(1, CountOf(extracted, "Quarterly report 2027"));
        Assert.Equal(0, CountOf(extracted, "Quarterly report 2026"));

        // The content of the new revision (the last content stream) has the new text and not the old; the first revision, the beginning of the file, still has the old.
        var streams = TextEditProbe.ContentStreams(bytes);
        Assert.Contains("Quarterly report 2027", TextEditProbe.ShownText(streams[^1]), StringComparison.Ordinal);
        Assert.DoesNotContain("Quarterly report 2026", TextEditProbe.ShownText(streams[^1]), StringComparison.Ordinal);
        Assert.Contains("Quarterly report 2026", TextEditProbe.ShownText(streams[0]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavedFileKeepsTheOriginalBytesAsPrefix()
    {
        var bytes = await EditAsync("text-edit-standard.pdf", 0, "Quarterly report 2026", (document, page, item) => SpikeEditor.Replace(document.Access(), page, item, "Quarterly report 2027"));
        var original = await File.ReadAllBytesAsync(TestData.Pdf("text-edit-standard.pdf"), Ct);

        Assert.True(bytes.AsSpan(0, original.Length).SequenceEqual(original));
        Assert.True(bytes.Length - original.Length < 4096, $"an edit of one line grew the file by {bytes.Length - original.Length} bytes");
        _ = Save(bytes, "text-edit-standard-edited.pdf");
    }

    [Fact]
    public async Task ExtractionOfTheSavedFileGivesTheNewText()
    {
        var bytes = await EditAsync("text-edit-subset.pdf", 0, "Invoice total: 1,250 EUR", (document, page, item) => SpikeEditor.Replace(document.Access(), page, item, "Invoice total: 1,205 EUR"));
        var path = Save(bytes, "text-edit-subset-edited.pdf");
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var text = await document.GetPageTextAsync(0, Ct);
        var all = text.GetText(0, text.Chars.Count);

        Assert.Contains("Invoice total: 1,205 EUR", all, StringComparison.Ordinal);
        Assert.DoesNotContain("1,250", all, StringComparison.Ordinal);
        Assert.Contains("Kontoauszug März", all, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RotatedRunStaysUprightInReadingDirection()
    {
        // The matrix of the object carries the turn; replacing the text in place keeps it, so no maths of our own is needed.
        var bytes = await EditAsync("text-edit-rotated.pdf", 0, "Turned page line", (document, page, item) => SpikeEditor.Replace(document.Access(), page, item, "Turned page lines"));
        var path = Save(bytes, "text-edit-rotated-edited.pdf");
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var text = await document.GetPageTextAsync(0, Ct);
        var all = text.GetText(0, text.Chars.Count);
        var at = all.IndexOf("Turned page lines", StringComparison.Ordinal);

        Assert.True(at >= 0, all);
        var boxes = text.Chars.Skip(at).Take("Turned page lines".Length).Select(static c => c.Box).ToArray();
        for (var i = 1; i < boxes.Length; i++)
        {
            Assert.True(boxes[i].Left > boxes[i - 1].Left - 0.01, "the characters run left to right on the displayed page");
            // The same band on every system: the boxes differ with the substitute font (Linux has another Helvetica), a wrongly turned line would differ by tens of points.
            Assert.Equal(boxes[0].Bottom, boxes[i].Bottom, 7.0);
        }

        var known = TextEditFixtures.Run("text-edit-rotated.pdf", "Turned page line");
        Assert.True(boxes[0].Inflate(known.Size * 0.6).Contains(known.Left, known.Baseline));
    }

    [Fact]
    public async Task TwoFontsInOneLineAreSetAtTheRightPlace()
    {
        var geist = TextBoxFontBytes();
        var before = await OnPageAsync("text-edit-subset.pdf", 0, static (_, page, textPage) => SpikeEditor.CharsOf(page, TextEditProbe.ReadTexts(page, textPage)[0].Object));
        var bytes = await EditAsync(
            "text-edit-subset.pdf",
            0,
            "Invoice total: 1,250 EUR",
            (document, page, item) =>
            {
                nint font;
                unsafe
                {
                    fixed (byte* data = geist)
                    {
                        font = Fpdf.FPDFText_LoadFont(document.Access(), data, (uint)geist.Length, FpdfConstants.FontTrueType, cid: 1);
                    }
                }

                Assert.NotEqual(0, font);
                SpikeEditor.ReplaceWithOtherFont(document.Access(), page, item, font, 19, 1, "9", 4);
            });
        var line = await OnSavedAsync(
            bytes,
            0,
            static (_, page, textPage) =>
            {
                var chars = new List<(char C, double X, double Y)>();
                foreach (var t in TextEditProbe.ReadTexts(page, textPage))
                {
                    var o = SpikeEditor.CharsOf(page, t.Object);
                    for (var i = 0; i < o.X.Length; i++)
                    {
                        if (Math.Abs(o.Y[i] - 170) < 1)
                        {
                            chars.Add(((char)o.Unicode[i], o.X[i], o.Y[i]));
                        }
                    }
                }

                return chars.OrderBy(static c => c.X).ToArray();
            });

        Assert.Equal("Invoice total: 1,259 EUR", new string(line.Select(static c => c.C).ToArray()));
        for (var i = 0; i < 19; i++)
        {
            Assert.Equal(before.X[i], line[i].X, 0.05);
        }

        Assert.Equal(before.X[19], line[19].X, 0.05);

        // The suffix keeps its own spacing: " EUR" moved as a whole by the width difference of "0" and "9" in the two fonts.
        var shift = line[20].X - before.X[20];
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(before.X[20 + i] + shift, line[20 + i].X, 0.05);
        }

        Assert.True(Math.Abs(shift) < 3, $"the suffix moved by {shift}");
    }

    [Fact]
    public async Task VerticalTextAndTextInFormsAndMarkedContentAreFoundOnTheirGeometryAndMarks()
    {
        var texts = await OnPageAsync("text-edit-locked.pdf", 0, static (_, page, textPage) => TextEditProbe.ReadTexts(page, textPage));

        // Text in a Form XObject is reported with its depth (shared and nested alike).
        Assert.Equal(1, texts.Single(static t => t.Text.StartsWith("Shared header", StringComparison.Ordinal)).Depth);
        Assert.Equal(2, texts.Single(static t => t.Text.StartsWith("Nested form", StringComparison.Ordinal)).Depth);

        // /ActualText replaces the text of the object in the extraction and has a mark parameter.
        var marked = texts.Single(static t => t.Marks.Length > 0);
        Assert.Equal("Replacement", marked.Text);
        Assert.Equal(["Span"], marked.Marks);
        var keys = await OnPageAsync("text-edit-locked.pdf", 0, static (_, page, textPage) => TextEditProbe.MarkKeys(TextEditProbe.ReadTexts(page, textPage).Single(static t => t.Marks.Length > 0).Object));
        Assert.Equal(["ActualText"], keys);

        // The modes: 3 invisible, 1 stroke, 4 clip.
        Assert.Equal(3, texts.Single(static t => t.Text == "Invisible text layer").Mode);
        Assert.Equal(1, texts.Single(static t => t.Text == "Stroked outline text").Mode);
        Assert.Equal(4, texts.Single(static t => t.Text == "Clipping text mode").Mode);

        // Vertical writing stacks the characters along the text-space y axis.
        var chars = await OnPageAsync("text-edit-locked.pdf", 0, static (_, page, textPage) => SpikeEditor.CharsOf(page, TextEditProbe.ReadTexts(page, textPage).Single(static t => t.Text == "Vertical").Object));
        var along = Math.Abs(chars.X[^1] - chars.X[0]) + Math.Abs(chars.Y[^1] - chars.Y[0]);
        Assert.True(along > 1);
    }

    [Fact]
    public async Task TextInAFormObjectIsNotWrittenByTheGenerator()
    {
        // A change inside a Form XObject does not reach the file: PDFium regenerates the page content only. Such text is locked.
        var bytes = await EditAsync("text-edit-locked.pdf", 0, "Shared header text", (document, page, item) => SpikeEditor.Replace(document.Access(), page, item, "Shared header test"));
        foreach (var pageIndex in new[] { 0, 1 })
        {
            var all = await OnSavedAsync(bytes, pageIndex, static (_, _, textPage) => TextEditProbe.TextOfPage(textPage));
            Assert.Contains("Shared header text", all, StringComparison.Ordinal);
            Assert.DoesNotContain("Shared header test", all, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheGeneratorLosesType3TextSoAPageWithItIsLocked()
    {
        // PDFium cannot write a Type 3 text object: after the page content is regenerated, "ABBA" is gone from the saved page, so every run of
        // such a page is locked (the whole page content is written again, not only the edited object).
        var bytes = await EditAsync("text-edit-locked.pdf", 2, "Line beside Type 3", (document, page, item) => SpikeEditor.Replace(document.Access(), page, item, "Line beside Type 3!"));
        var all = await OnSavedAsync(bytes, 2, static (_, _, textPage) => TextEditProbe.TextOfPage(textPage));

        Assert.Contains("Line beside Type 3!", all, StringComparison.Ordinal);
        Assert.DoesNotContain("ABBA", all, StringComparison.Ordinal);
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static byte[] TextBoxFontBytes() => Pagewing.Core.Annotations.TextBoxFont.ReadData();
}

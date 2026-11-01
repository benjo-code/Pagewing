using System.Globalization;
using System.Text;
using Pagewing.Core.Pdf;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Pdfium;
using Pagewing.Pdfium.Native;
using Pagewing.TestSupport;
using Xunit;
using static Pagewing.Core.Tests.Pdfium.RedactionProbe;

namespace Pagewing.Core.Tests.Pdfium;

/// <summary>
/// The spike of milestone 14 (<c>Docs/DECISIONS.md</c>, "Schwärzen: Spike und QPDF-Entscheidung (M14)"): what PDFium can do for
/// redaction. The assertions are the behaviour found with PDFium 157.0.8100 and guard against a PDFium update that changes it: such
/// a change is a finding to look at (the loss list and the fallbacks of the redaction are written from these facts), never an
/// expectation to loosen. Set <c>PAGEWING_SPIKE_OUT</c> to a folder to keep every result file there (for <c>qpdf --check</c>).
/// </summary>
public sealed class RedactionSpikeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void KeepForQpdf(string name, byte[] bytes)
    {
        var output = Environment.GetEnvironmentVariable("PAGEWING_SPIKE_OUT");
        if (!string.IsNullOrEmpty(output))
        {
            Directory.CreateDirectory(output);
            File.WriteAllBytes(Path.Combine(output, name + ".pdf"), bytes);
        }
    }

    private static FsMatrix Multiply(FsMatrix a, FsMatrix b) => new()
    {
        A = (a.A * b.A) + (a.B * b.C),
        B = (a.A * b.B) + (a.B * b.D),
        C = (a.C * b.A) + (a.D * b.C),
        D = (a.C * b.B) + (a.D * b.D),
        E = (a.E * b.A) + (a.F * b.C) + b.E,
        F = (a.E * b.B) + (a.F * b.D) + b.F,
    };

    [Fact]
    public async Task FormObjectEditsAreWrittenBack()
    {
        var thread = PdfiumThread.Shared;
        using var folder = new TempFolder();
        var path = TestData.Pdf("redact-everywhere.pdf");
        var original = await File.ReadAllBytesAsync(path, Ct);
        Log($"Q1 original: raw {RawCount(original, Term)}");

        // A: edit the content of the form object in place and generate the page content.
        var edited = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, path, null);
                using var page = PdfiumRedaction.OpenPage(document, 0);
                var form = Enumerable.Range(0, page.CountObjects()).Select(page.GetObject).First(o => page.Describe(o).Type == FpdfConstants.PageObjForm);
                var inner = page.CountFormObjects(form);
                var types = Enumerable.Range(0, inner).Select(i => page.Describe(page.GetFormObject(form, i)).Type).ToArray();
                var text = page.GetFormObject(form, 0);
                var removed = page.RemoveFromForm(form, text);
                page.Destroy(text);
                var innerAfter = page.CountFormObjects(form);
                var generated = page.GenerateContent();
                var bytes = Save(document);
                return (inner, types, removed, innerAfter, generated, bytes);
            },
            cancellationToken: Ct);
        Log($"Q1 A: inner={edited.inner} types=[{string.Join(",", edited.types)}] removed={edited.removed} innerAfter={edited.innerAfter} generated={edited.generated} raw {RawCount(edited.bytes, Term)} bytes {edited.bytes.Length} vs {original.Length}");
        var editedPath = Path.Combine(folder.Path, "q1a.pdf");
        await File.WriteAllBytesAsync(editedPath, edited.bytes, Ct);
        KeepForQpdf("q1a-form-inplace", edited.bytes);
        var lettersA = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, editedPath, null);
                return (PageLetters(document, 0), PageLetters(document, 1));
            },
            cancellationToken: Ct);
        Log($"Q1 A letters page1 '{lettersA.Item1}' page2 '{lettersA.Item2}'");

        // The form object lost its one child in memory and the page content was generated ...
        Assert.Equal(1, edited.inner);
        Assert.Equal([FpdfConstants.PageObjText], edited.types);
        Assert.True(edited.removed);
        Assert.Equal(0, edited.innerAfter);
        Assert.True(edited.generated);

        // ... but the stream of the form XObject was not written: after reopening, the page still shows the text of the form (three times:
        // visible, in the form, invisible) and the stream of the form still holds the term.
        const string Three = "Adresse:Zephyrstraße17Zephyrstraße17Zephyrstraße17Scanabovecarriesinvisibletext.";
        Assert.Equal("Redactionfixture,pageone" + Three, lettersA.Item1);
        Assert.Equal("SeitezweiZephyrstraße17", lettersA.Item2);
        Assert.Contains(Where(edited.bytes, Term), hit => hit.Contains("/BBox[ 0 0 150 30]", StringComparison.Ordinal));

        // B: flatten the form object into the page (the children move to the page with the matrix of the form applied), then split the
        // text that was in the form like any other text object.
        var flat = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, path, null);
                using var page = PdfiumRedaction.OpenPage(document, 0);
                var objects = Enumerable.Range(0, page.CountObjects()).Select(page.GetObject).ToList();
                var form = objects.First(o => page.Describe(o).Type == FpdfConstants.PageObjForm);
                var index = objects.IndexOf(form);
                var gotFormMatrix = page.TryGetMatrix(form, out var formMatrix);
                var moved = 0;
                var childMatrices = new List<FsMatrix>();
                while (page.CountFormObjects(form) > 0)
                {
                    var child = page.GetFormObject(form, 0);
                    Assert.True(page.TryGetMatrix(child, out var childMatrix));
                    childMatrices.Add(childMatrix);
                    Assert.True(page.RemoveFromForm(form, child));
                    Assert.True(page.SetMatrix(child, Multiply(childMatrix, formMatrix)));
                    Assert.True(page.InsertAt(child, index + moved));
                    moved++;
                }

                Assert.True(page.Remove(form));
                page.Destroy(form);

                ProbeRect? rect;
                var textPage = page.LoadTextPage();
                try
                {
                    rect = FindTerm(ReadChars(textPage).Where(c => c.OriginY is > 295 and < 320), Term);
                }
                finally
                {
                    page.CloseTextPage(textPage);
                }

                var decisions = DecomposeText(document, page, [rect!.Value]);
                Assert.True(page.GenerateContent());
                Verify(document, 0, decisions);
                return (gotFormMatrix, formMatrix, childMatrices, moved, rect, decisions, Save(document));
            },
            cancellationToken: Ct);
        Log($"Q1 B form matrix [{flat.formMatrix.A} {flat.formMatrix.B} {flat.formMatrix.C} {flat.formMatrix.D} {flat.formMatrix.E} {flat.formMatrix.F}] children={flat.moved} text rect {flat.rect}");
        Log($"Q1 B flattened and split: raw {RawCount(flat.Item7, Term)}");
        KeepForQpdf("q1b-form-flattened-split", flat.Item7);
        Assert.True(flat.gotFormMatrix);
        Assert.Equal(1, flat.moved);
        Assert.Equal((1f, 0f, 0f, 1f, 20f, 300f), (flat.formMatrix.A, flat.formMatrix.B, flat.formMatrix.C, flat.formMatrix.D, flat.formMatrix.E, flat.formMatrix.F));
        Assert.Equal((1f, 0f, 0f, 1f, 2f, 10f), (flat.childMatrices[0].A, flat.childMatrices[0].B, flat.childMatrices[0].C, flat.childMatrices[0].D, flat.childMatrices[0].E, flat.childMatrices[0].F));
        var decision = Assert.Single(flat.decisions, d => d.Text == Term);
        Assert.Equal(TextOutcome.RemovedAll, decision.Outcome);

        var flatPath = Path.Combine(folder.Path, "q1b.pdf");
        await File.WriteAllBytesAsync(flatPath, flat.Item7, Ct);
        var pageLetters = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, flatPath, null);
                return (PageLetters(document, 0), PageLetters(document, 1));
            },
            cancellationToken: Ct);
        Log($"Q1 B letters page1 '{pageLetters.Item1}' page2 '{pageLetters.Item2}'");

        // Page one lost the term of the form, page two still has it, and the form XObject is still in the file for page two.
        Assert.Equal("Redactionfixture,pageoneAdresse:Zephyrstraße17Zephyrstraße17Scanabovecarriesinvisibletext.", pageLetters.Item1);
        Assert.Equal("SeitezweiZephyrstraße17", pageLetters.Item2);
        Assert.Contains(Where(flat.Item7, Term), hit => hit.Contains("/BBox[ 0 0 150 30]", StringComparison.Ordinal));

        // Outside the pattern fill (which the page content generator loses, see ThePageContentGeneratorDamagesWhatItCannotWrite) and the
        // removed text the page looks as before.
        var engine = new PdfEngine();
        var beforeRender = await RedactionRender.RenderAsync(engine, path, 2);
        var flatRender = await RedactionRender.RenderAsync(engine, flatPath, 2);
        var (d, n, m) = CompareOutside(beforeRender, flatRender, 2, [new ProbeRect(20, 250, 220, 280), flat.rect!.Value], 1.5);
        Log($"Q1 B render of the flattened and split page vs original outside the pattern and the text: different={d} of {n} max={m}");
        Assert.Equal(0, d);
    }

    private static async Task<(byte[] Bytes, string Path)> WriteAgainAsync(PdfiumThread thread, TempFolder folder, string name, bool touchEveryObject)
    {
        var path = TestData.Pdf(name);
        var bytes = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, path, null);
                for (var i = 0; i < document.PageCount; i++)
                {
                    using var page = PdfiumRedaction.OpenPage(document, i);
                    if (touchEveryObject)
                    {
                        // Take every object out and put it back, so that the whole page content is written again.
                        var count = page.CountObjects();
                        for (var k = 0; k < count; k++)
                        {
                            var obj = page.GetObject(k);
                            Assert.True(page.Remove(obj));
                            Assert.True(page.InsertAt(obj, k));
                        }
                    }

                    Assert.True(page.GenerateContent());
                }

                return Save(document);
            },
            cancellationToken: Ct);
        var resultPath = Path.Combine(folder.Path, (touchEveryObject ? "touched-" : "plain-") + name);
        await File.WriteAllBytesAsync(resultPath, bytes, Ct);
        return (bytes, resultPath);
    }

    /// <summary>
    /// The page content generator (<c>FPDFPage_GenerateContent</c>) leaves a page that nothing touched as it is, but writes the whole
    /// page content again as soon as one object is changed, and it cannot write everything it reads: a tiling pattern fill, a shading,
    /// a Type 3 text, an inline image, a text with character or word spacing or horizontal scaling and a text in a CID font with a
    /// CIDToGIDMap stream come out different. So every page that is edited needs a render check outside the edit (Docs/DECISIONS.md).
    /// </summary>
    [Fact]
    public async Task ThePageContentGeneratorDamagesWhatItCannotWrite()
    {
        var thread = PdfiumThread.Shared;
        using var folder = new TempFolder();
        var engine = new PdfEngine();
        string[] clean = ["redact-paths.pdf", "redact-image.pdf", "redact-rotated.pdf", "redact-structure.pdf", "text-rich.pdf", "annotations.pdf", "transparency.pdf", "image-rgb.pdf"];
        string[] all = [.. clean, "redact-everywhere.pdf", "redact-hidden.pdf", "redact-image-kinds.pdf", "redact-fonts.pdf", "vector-shapes.pdf"];

        // 1. Nothing touched: the page is not written again and looks the same everywhere.
        foreach (var name in all)
        {
            var (_, resultPath) = await WriteAgainAsync(thread, folder, name, touchEveryObject: false);
            var before = await RedactionRender.RenderAsync(engine, TestData.Pdf(name), 2);
            var after = await RedactionRender.RenderAsync(engine, resultPath, 2);
            Assert.Equal(0, CompareOutside(before, after, 2, [], 0).Different);
        }

        // 2. Every object touched: what the generator can write comes out the same ...
        foreach (var name in all)
        {
            var (bytes, resultPath) = await WriteAgainAsync(thread, folder, name, touchEveryObject: true);
            KeepForQpdf("gc-" + Path.GetFileNameWithoutExtension(name), bytes);
            var before = await RedactionRender.RenderAsync(engine, TestData.Pdf(name), 2);
            var after = await RedactionRender.RenderAsync(engine, resultPath, 2);
            var (different, compared, max) = CompareOutside(before, after, 2, [], 0);
            var box = DiffBox(before, after, 2);
            Log($"GC {name}: different={different} of {compared} max={max} box={box}");
            if (clean.Contains(name))
            {
                Assert.Equal(0, different);
                continue;
            }

            // ... and what it cannot write is damaged exactly where that object is.
            Assert.True(different > 0, name);
            var expected = name switch
            {
                "redact-everywhere.pdf" => new ProbeRect(20, 250, 220, 280), // the tiling pattern fill
                "redact-image-kinds.pdf" => new ProbeRect(230, 16, 330, 116), // the inline image
                "vector-shapes.pdf" => new ProbeRect(160, 30, 280, 110), // the shading
                _ => (ProbeRect?)null,
            };
            if (expected is { } e)
            {
                Assert.True(box is { } b && Math.Abs(b.Left - e.Left) < 1.5 && Math.Abs(b.Bottom - e.Bottom) < 1.5 && Math.Abs(b.Right - e.Right) < 1.5 && Math.Abs(b.Top - e.Top) < 1.5, $"{name}: {box}");
            }

            if (name == "redact-hidden.pdf")
            {
                Assert.True(CompareOutside(before, after, 2, [], 0, new ProbeRect(15, 235, 110, 285)).Different > 0); // the Type 3 text
                Assert.Equal(0, CompareOutside(before, after, 2, [], 0, new ProbeRect(15, 145, 225, 215)).Different); // the soft-masked rectangle
                Assert.True(CompareOutside(before, after, 2, [], 0, new ProbeRect(15, 75, 225, 125)).Different > 0); // the shading
            }

            if (name == "redact-fonts.pdf")
            {
                // Lines 0, 1, 2, 4, 5, 6 are written back exactly; line 3 (CIDToGIDMap stream), line 7 (Tc, Tw, Tz) and the turned line (Tz still in force) are not.
                bool[] damaged = [false, false, false, true, false, false, false, true];
                for (var line = 0; line < damaged.Length; line++)
                {
                    var y = 390 - (30 * line);
                    var lineDifferent = CompareOutside(before, after, 2, [], 0, new ProbeRect(0, y - 6, 420, y + 14)).Different;
                    Assert.Equal(damaged[line], lineDifferent > 0);
                }

                Assert.True(CompareOutside(before, after, 2, [], 0, new ProbeRect(0, 40, 260, 200)).Different > 0);
            }
        }
    }

    private static int CountBytes(byte[] haystack, byte[] pattern)
    {
        if (pattern.Length == 0)
        {
            return 0;
        }

        var count = 0;
        var span = haystack.AsSpan();
        int at;
        while ((at = span.IndexOf(pattern)) >= 0)
        {
            count++;
            span = span[(at + pattern.Length)..];
        }

        return count;
    }

    [Fact]
    public async Task ImageOverwriteKeepsMaskAndRecodesLosslessly()
    {
        var thread = PdfiumThread.Shared;
        using var folder = new TempFolder();
        foreach (var name in new[] { "redact-image.pdf", "redact-image-kinds.pdf" })
        {
            var path = TestData.Pdf(name);
            var original = await File.ReadAllBytesAsync(path, Ct);
            var run = await thread.InvokeAsync(
                () =>
                {
                    using var document = PdfiumDocument.Open(thread, path, null);
                    using var page = PdfiumRedaction.OpenPage(document, 0);
                    var images = Enumerable.Range(0, page.CountObjects()).Select(page.GetObject).Where(o => page.Describe(o).Type == FpdfConstants.PageObjImage).ToList();
                    var results = new List<string>();
                    var raws = new List<byte[]>();
                    var befores = new List<(int W, int H, int F, byte[] Bgra)?>();
                    var regions = new List<(int X0, int X1, int Y0, int Y1)>();
                    foreach (var image in images)
                    {
                        var facts = ReadImageFacts(page, image);
                        raws.Add(facts.Raw);
                        var pixels = ReadImagePixels(page, image, facts.Width, facts.Height);
                        befores.Add(pixels);
                        var info = $"filters=[{string.Join(",", facts.Filters)}] {facts.Width}x{facts.Height} cs={facts.ColorSpace} bpp={facts.BitsPerPixel} raw={facts.RawLength} decoded={facts.DecodedLength} read={(pixels is null ? "FAILED" : $"{pixels.Value.Width}x{pixels.Value.Height} format={pixels.Value.Format}")}";
                        results.Add(info);
                        if (pixels is null)
                        {
                            regions.Add((0, 0, 0, 0));
                            continue;
                        }

                        var (w, h, _, bgra) = pixels.Value;
                        var region = (X0: w * 3 / 10, X1: w * 7 / 10, Y0: h / 4, Y1: h * 3 / 4);
                        regions.Add(region);
                        var edited = (byte[])bgra.Clone();
                        for (var y = region.Y0; y < region.Y1; y++)
                        {
                            for (var x = region.X0; x < region.X1; x++)
                            {
                                var i = ((y * w) + x) * 4;
                                (edited[i], edited[i + 1], edited[i + 2], edited[i + 3]) = (0, 0, 0, 255);
                            }
                        }

                        var written = WriteImagePixels(page, image, w, h, edited);
                        results.Add($"  set bitmap: {written}");
                    }

                    Assert.True(page.GenerateContent());
                    return (images.Count, results, raws, befores, regions, Save(document));
                },
                cancellationToken: Ct);
            foreach (var line in run.results)
            {
                Log($"Q3 {name} {line}");
            }

            var resultPath = Path.Combine(folder.Path, name);
            await File.WriteAllBytesAsync(resultPath, run.Item6, Ct);
            KeepForQpdf("q3-" + Path.GetFileNameWithoutExtension(name), run.Item6);

            // Read back from the saved file.
            var after = await thread.InvokeAsync(
                () =>
                {
                    using var document = PdfiumDocument.Open(thread, resultPath, null);
                    using var page = PdfiumRedaction.OpenPage(document, 0);
                    var images = Enumerable.Range(0, page.CountObjects()).Select(page.GetObject).Where(o => page.Describe(o).Type == FpdfConstants.PageObjImage).ToList();
                    return images.Select(image =>
                    {
                        var facts = ReadImageFacts(page, image);
                        return (facts, ReadImagePixels(page, image, facts.Width, facts.Height));
                    }).ToList();
                },
                cancellationToken: Ct);

            Log($"Q3 {name}: images before={run.Item1} after={after.Count}");
            Assert.Equal(name == "redact-image.pdf" ? 3 : 4, run.Item1);
            Assert.Equal(name == "redact-image.pdf" ? 3 : 3, after.Count); // the inline image does not survive writing the page again
            for (var i = 0; i < after.Count; i++)
            {
                var (facts, pixels) = after[i];

                // The inline image (third of the second file) is gone after the page was written again: the others keep their order.
                var source = run.Item1 != after.Count && i >= 2 ? i + 1 : i;
                var before = run.befores[source];
                var raw = run.raws[source];
                var orphan = raw.Length >= 16 ? CountBytes(run.Item6, raw) : -1;
                if (pixels is null || before is null)
                {
                    Log($"Q3 {name} image {i}: after read={(pixels is null ? "FAILED" : "ok")} before={(before is null ? "FAILED" : "ok")}");
                    continue;
                }

                var (w, h, _, bgra) = pixels.Value;
                var (bw, bh, _, bbgra) = before.Value;
                var region = run.regions[source];
                var insideOk = 0;
                var insideTotal = 0;
                var outsideSame = 0;
                var outsideTotal = 0;
                var alphaChanged = 0;
                for (var y = 0; y < Math.Min(h, bh); y++)
                {
                    for (var x = 0; x < Math.Min(w, bw); x++)
                    {
                        var idx = ((y * w) + x) * 4;
                        var bidx = ((y * bw) + x) * 4;
                        var inside = x >= region.X0 && x < region.X1 && y >= region.Y0 && y < region.Y1;
                        if (inside)
                        {
                            insideTotal++;
                            if (bgra[idx] == 0 && bgra[idx + 1] == 0 && bgra[idx + 2] == 0 && bgra[idx + 3] == 255)
                            {
                                insideOk++;
                            }
                        }
                        else
                        {
                            outsideTotal++;
                            var same = true;
                            for (var c = 0; c < 4; c++)
                            {
                                if (bgra[idx + c] != bbgra[bidx + c])
                                {
                                    same = false;
                                }
                            }

                            if (same)
                            {
                                outsideSame++;
                            }

                            if (bgra[idx + 3] != bbgra[bidx + 3])
                            {
                                alphaChanged++;
                            }
                        }
                    }
                }

                Assert.Equal(insideTotal, insideOk); // the pixels in the area are the fill, opaque
                Assert.Equal(outsideTotal, outsideSame); // every other pixel of the image, alpha included, is exactly as it was
                Assert.Equal(0, alphaChanged);
                Assert.Equal(0, orphan); // the stream of the old image is not in the file any more
                Assert.Equal(["FlateDecode"], facts.Filters); // re-encoded losslessly as Flate, as RGB
                Log($"Q3 {name} image {i} (was {source}): after filters=[{string.Join(",", facts.Filters)}] {facts.Width}x{facts.Height} cs={facts.ColorSpace} bpp={facts.BitsPerPixel}; inside black {insideOk}/{insideTotal}; outside same {outsideSame}/{outsideTotal} alphaChanged={alphaChanged}; old raw stream still in file: {orphan}");
            }
        }
    }

    /// <summary>A minimal PDF with <paramref name="pages"/> (0 or 1) empty pages: the seed that the pages of a document are imported into.</summary>
    private static byte[] SeedPdf(int pages)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            pages == 0 ? "<< /Type /Pages /Count 0 /Kids [] >>" : "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
        };
        if (pages > 0)
        {
            objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] >>");
        }

        var output = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(output.Length);
            output.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = output.Length;
        output.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            output.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        output.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(output.ToString());
    }

    [Fact]
    public async Task ImportIntoANewDocumentDropsInfoXmpOutlinesAttachmentsScripts()
    {
        var thread = PdfiumThread.Shared;
        using var folder = new TempFolder();
        foreach (var pages in new[] { 0, 1 })
        {
            var seedPath = Path.Combine(folder.Path, $"seed{pages}.pdf");
            await File.WriteAllBytesAsync(seedPath, SeedPdf(pages), Ct);
            var openable = await thread.InvokeAsync(
                () =>
                {
                    try
                    {
                        using var seed = PdfiumDocument.Open(thread, seedPath, null);
                        return $"opens, {seed.PageCount} page(s)";
                    }
                    catch (PdfiumOpenException exception)
                    {
                        return $"does not open: {exception.Error}";
                    }
                },
                cancellationToken: Ct);
            Log($"Q4 seed with {pages} page(s): {openable}");
        }

        foreach (var (name, sourcePages) in new[] { ("redact-everywhere.pdf", new[] { 0, 1 }), ("redact-structure.pdf", new[] { 0 }) })
        {
            var path = TestData.Pdf(name);
            var original = await File.ReadAllBytesAsync(path, Ct);
            Log($"Q4 {name} original keys: {string.Join(" ", KeysIn(original))}");

            foreach (var seedPages in new[] { 0, 1 })
            {
                var seedPath = Path.Combine(folder.Path, $"seed{seedPages}.pdf");
                byte[]? bytes;
                try
                {
                    bytes = await thread.InvokeAsync(
                        () =>
                        {
                            using var seed = PdfiumDocument.Open(thread, seedPath, null);
                            using var source = PdfiumDocument.Open(thread, path, null);
                            PdfiumPageEdit.Import(seed, source, sourcePages, 0);
                            if (seedPages == 1)
                            {
                                PdfiumPageEdit.Delete(seed, sourcePages.Length);
                            }

                            return Save(seed);
                        },
                        cancellationToken: Ct);
                }
                catch (PdfiumOpenException)
                {
                    continue;
                }

                KeepForQpdf($"q4-{Path.GetFileNameWithoutExtension(name)}-seed{seedPages}", bytes);
                Log($"Q4 {name} imported into seed{seedPages}: {bytes.Length} bytes, keys: {string.Join(" ", KeysIn(bytes))}");
                foreach (var hit in Where(bytes, Term))
                {
                    Log($"Q4   term in: {hit}");
                }

                foreach (var hit in Where(bytes, "Quellmann"))
                {
                    Log($"Q4   second term in: {hit}");
                }

                var resultPath = Path.Combine(folder.Path, $"q4-{name}-{seedPages}.pdf");
                await File.WriteAllBytesAsync(resultPath, bytes, Ct);
                var info = await thread.InvokeAsync(
                    () =>
                    {
                        using var document = PdfiumDocument.Open(thread, resultPath, null);
                        return (document.PageCount, document.GetMetaText("Title"), document.GetMetaText("Producer"), document.GetMetaText("Author"), document.GetMetaText("Subject"), document.FormType);
                    },
                    cancellationToken: Ct);
                Log($"Q4   reopened: {info}");

                // No information dictionary at all (not even a producer), no AcroForm, whatever the seed.
                Assert.Equal(sourcePages.Length, info.PageCount);
                Assert.True(string.IsNullOrEmpty(info.Item2) && string.IsNullOrEmpty(info.Item3) && string.IsNullOrEmpty(info.Item4) && string.IsNullOrEmpty(info.Item5));
                Assert.Equal(FpdfConstants.FormTypeNone, info.FormType);
                Assert.Empty(Where(bytes, "Quellmann"));
                if (name == "redact-everywhere.pdf")
                {
                    // Document level is gone: outline, attachments, JavaScript, XMP, info, form. What stays are the page contents and the annotations
                    // (note, reply, widget with its value): seven places, as in the file with the pages' own objects.
                    Assert.Equal(["/Annots", "/IRT", "/Pattern"], KeysIn(bytes));
                    Assert.Equal(7, Where(bytes, Term).Count);
                }
                else
                {
                    // What the page dictionary carries comes along with the page: thumbnail, private data, page XMP, page actions (JavaScript),
                    // the optional content layer and the annotations (a Link, the widget of a form field without its parent field).
                    Assert.Equal(["/Metadata", "/JavaScript", "/Thumb", "/PieceInfo", "/AA", "/StructParents", "/OC", "/Annots"], KeysIn(bytes));
                    Assert.Equal(8, Where(bytes, Term).Count);
                    Assert.Contains(Where(bytes, Term), hit => hit.Contains("/Subtype/XML", StringComparison.Ordinal));
                    Assert.Contains(Where(bytes, Term), hit => hit.Contains("/PieceInfo", StringComparison.Ordinal));
                    Assert.Contains(Where(bytes, Term), hit => hit.Contains("var other", StringComparison.Ordinal));
                    Assert.DoesNotContain(Where(bytes, Term), hit => hit.Contains("/T(Group)", StringComparison.Ordinal)); // the parent field with /V and /DV is not copied
                }
            }

            // The same document rewritten as it is, for comparison.
            var direct = await thread.InvokeAsync(
                () =>
                {
                    using var document = PdfiumDocument.Open(thread, path, null);
                    return Save(document);
                },
                cancellationToken: Ct);
            Log($"Q4 {name} plain rewrite: {direct.Length} bytes, keys: {string.Join(" ", KeysIn(direct))}");

            // For comparison, the document written as it is keeps all of it.
            var kept = KeysIn(direct);
            string[] documentLevel = name == "redact-everywhere.pdf"
                ? ["/Outlines", "/Names", "/EmbeddedFile", "/Metadata", "/AcroForm", "/Author"]
                : ["/Names", "/OpenAction", "/PageLabels", "/StructTreeRoot", "/OCProperties", "/AcroForm", "/Metadata", "/Keywords"];
            Assert.All(documentLevel, key => Assert.Contains(key, kept));
            foreach (var hit in Where(direct, Term))
            {
                Log($"Q4   term in: {hit}");
            }
        }
    }

    /// <summary>The share of text objects the trial decomposition splits and the read-back verifies, over all fixtures that have text.</summary>
    [Fact]
    public async Task TextObjectsOfTheFixturesSplitAndVerify()
    {
        var thread = PdfiumThread.Shared;
        var total = 0;
        var verified = 0;
        foreach (var name in new[] { "redact-everywhere.pdf", "redact-covered.pdf", "redact-revisions.pdf", "redact-rotated.pdf", "redact-fonts.pdf", "redact-structure.pdf", "text-rich.pdf", "text-basic.pdf", "hello_world.pdf", "pages-marked.pdf", "annotations.pdf", "text-dense.pdf", "forms-basic.pdf" })
        {
            var path = TestData.Pdf(name);
            var decisions = await thread.InvokeAsync(
                () =>
                {
                    using var document = PdfiumDocument.Open(thread, path, null);
                    using var page = PdfiumRedaction.OpenPage(document, 0);

                    // Two of every seven characters of the page are "under a rectangle": every long object keeps characters on both sides.
                    var result = DecomposeText(document, page, [], c => c.Index % 7 is 3 or 4);
                    Assert.True(page.GenerateContent());
                    Verify(document, 0, result);
                    return result;
                },
                cancellationToken: Ct);
            var partial = decisions.Where(d => d.Kept > 0 && d.Removed > 0).ToList();
            var ok = partial.Count(d => d.Outcome == TextOutcome.Split && d.Verified == true);
            total += partial.Count;
            verified += ok;
            Log($"Q2 aggregate {name}: objects with survivors {partial.Count}, split and verified {ok}, split but not verified {partial.Count(d => d.Outcome == TextOutcome.Split && d.Verified != true)}, removed whole {partial.Count(d => d.Outcome == TextOutcome.RemovedWhole)}");
            foreach (var d in partial.Where(d => d.Outcome != TextOutcome.Split || d.Verified != true))
            {
                Log($"Q2 aggregate   not ok: {Describe(d)}");
            }
        }

        Log($"Q2 aggregate total: {verified} of {total} text objects split and verified");

        // 97 of 100: one object in a form XObject (the form is flattened first) and the two fonts of redact-fonts that PDFium cannot write.
        Assert.Equal(100, total);
        Assert.Equal(97, verified);
    }

    /// <summary>Character codes can be written (FPDFText_SetCharcodes) but never read: what that does for the renumbered CID font.</summary>
    [Fact]
    public async Task CharcodesCanBeSetButNotRead()
    {
        var thread = PdfiumThread.Shared;
        using var folder = new TempFolder();
        var engine = new PdfEngine();
        var path = TestData.Pdf("redact-fonts.pdf");
        var before = await RedactionRender.RenderAsync(engine, path, 2);
        var sentence = "Name: Zephyrstraße 17 ok";
        var distinct = sentence.Distinct().ToArray();
        var bytes = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, path, null);
                using var page = PdfiumRedaction.OpenPage(document, 0);
                var objects = Enumerable.Range(0, page.CountObjects()).Select(page.GetObject).ToList();

                // Line 3 (y = 300): the renumbered CID font. Replace the object by a new one that gets the codes of the fixture (CID = number in the distinct characters).
                var old = objects[3];
                var font = Fpdf.FPDFTextObj_GetFont(old);
                float size = 0;
                Assert.True(page.TryGetMatrix(old, out var matrix));
                var created = Fpdf.FPDFPageObj_CreateTextObj(page.NativeDocument, font, 14);
                var codes = sentence.Select(c => (uint)(Array.IndexOf(distinct, c) + 1)).ToArray();
                Assert.True(SetCharcodes(created, codes));
                Assert.True(page.SetMatrix(created, matrix));
                ReplaceObject(page, old, [created]);
                _ = size;
                Assert.True(page.GenerateContent());
                return Save(document);
            },
            cancellationToken: Ct);
        var resultPath = Path.Combine(folder.Path, "charcodes.pdf");
        await File.WriteAllBytesAsync(resultPath, bytes, Ct);
        var after = await RedactionRender.RenderAsync(engine, resultPath, 2);
        var (different, compared, max) = CompareOutside(before, after, 2, [], 0, new ProbeRect(0, 294, 420, 314));
        Log($"Q2 SetCharcodes on the renumbered CID line: different={different} of {compared} max={max}");

        // The same damage as when the original object is only written again: the codes are not the problem, the writing of that font is.
        Assert.True(different > 0);
    }

    [Fact]
    public async Task PathCropAndBackgroundNeverVanishesWhole()
    {
        var thread = PdfiumThread.Shared;
        using var folder = new TempFolder();
        var engine = new PdfEngine();
        var path = TestData.Pdf("redact-paths.pdf");
        var before = await RedactionRender.RenderAsync(engine, path, 2);

        // The hole has fractional edges on purpose: strips that meet at such an edge can show a seam.
        var hole = new ProbeRect(30.3, 190.7, 110.1, 260.9);
        var run = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, path, null);
                using var page = PdfiumRedaction.OpenPage(document, 0);
                var report = new List<string>();
                var objects = Enumerable.Range(0, page.CountObjects()).Select(page.GetObject).ToList();
                foreach (var obj in objects)
                {
                    var info = page.Describe(obj);
                    if (info.Type != FpdfConstants.PageObjPath)
                    {
                        report.Add($"object type={info.Type} bounds=({info.Left:0.#},{info.Bottom:0.#},{info.Right:0.#},{info.Top:0.#})");
                        continue;
                    }

                    var facts = ReadPath(page, obj);
                    var bounds = new ProbeRect(info.Left, info.Bottom, info.Right, info.Top);
                    var touches = bounds.Overlaps(hole);
                    var points = string.Join(" ", facts.Segments.Select(sg => $"{sg.Type}:{sg.X:0.#},{sg.Y:0.#}{(sg.Close ? "c" : string.Empty)}"));
                    var axisParallel = facts.SegmentCount is 4 or 5

                        // Only moves and lines: the control points of a Bezier curve can lie on the corners of a rectangle (the orange arc does).
                        && facts.Segments.All(sg => sg.Type is FpdfConstants.SegmentMoveTo or FpdfConstants.SegmentLineTo)
                        && Math.Abs(facts.Matrix.B) < 1e-6 && Math.Abs(facts.Matrix.C) < 1e-6
                        && facts.Fill != FpdfConstants.FillModeNone && !facts.Stroke
                        && facts.Segments.Zip(facts.Segments.Skip(1), (p, q) => Math.Abs(p.X - q.X) < 1e-4 || Math.Abs(p.Y - q.Y) < 1e-4).All(x => x);
                    report.Add($"path bounds=({info.Left:0.#},{info.Bottom:0.#},{info.Right:0.#},{info.Top:0.#}) fill={facts.Fill} stroke={facts.Stroke} fillRGBA=[{string.Join(",", facts.FillColor)}] segments={facts.SegmentCount} matrix=[{facts.Matrix.A:0.###} {facts.Matrix.B:0.###} {facts.Matrix.C:0.###} {facts.Matrix.D:0.###} {facts.Matrix.E:0.###} {facts.Matrix.F:0.###}] points={points} touches={touches} axisParallelFilledRect={axisParallel}");
                    if (touches && axisParallel)
                    {
                        var strips = CropRectangle(page, bounds, hole, facts.Fill, facts.FillColor);
                        ReplaceObject(page, obj, strips);
                        report.Add($"  -> cropped into {strips.Count} strip(s)");
                    }
                }

                Assert.True(page.GenerateContent());
                return (report, Save(document));
            },
            cancellationToken: Ct);
        foreach (var line in run.report)
        {
            Log("Q7 " + line);
        }

        var resultPath = Path.Combine(folder.Path, "paths-cropped.pdf");
        await File.WriteAllBytesAsync(resultPath, run.Item2, Ct);
        KeepForQpdf("q7-paths-cropped", run.Item2);
        var after = await RedactionRender.RenderAsync(engine, resultPath, 2);
        var (different, compared, max) = CompareOutside(before, after, 2, [hole], 1.0);
        Log($"Q7 render outside the hole (1 pt margin): different={different} of {compared} max={max} box={DiffBox(before, after, 2)}");
        var (insideDifferent, insideCompared, _) = CompareOutside(before, after, 2, [], 0, hole);
        Log($"Q7 render inside the hole (nothing is filled yet): different={insideDifferent} of {insideCompared}");

        // The background (a path over the whole page) became four strips, the blue rectangle two; the turned square, the triangle, the arc
        // and the stroked rectangle do not touch the hole here; outside the hole (and one point around it) the page is as before, with no
        // seam between the strips although the hole has fractional edges; inside nothing is painted yet.
        Assert.Equal(2, run.report.Count(line => line.StartsWith("  -> cropped into", StringComparison.Ordinal)));
        Assert.Contains("  -> cropped into 4 strip(s)", run.report);
        Assert.Contains("  -> cropped into 2 strip(s)", run.report);
        Assert.Single(run.report, line => line.StartsWith("path bounds=(230,100,280,150)", StringComparison.Ordinal) && line.EndsWith("axisParallelFilledRect=False", StringComparison.Ordinal));
        Assert.Equal(0, different);
        Assert.True(insideDifferent > 0);
    }

    [Fact]
    public async Task FullRewriteLeavesNoOlderRevision()
    {
        var thread = PdfiumThread.Shared;
        var path = TestData.Pdf("redact-revisions.pdf");
        var original = await File.ReadAllBytesAsync(path, Ct);
        var written = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, path, null);
                return (Save(document), PageLetters(document, 0));
            },
            cancellationToken: Ct);
        Log($"Q5 original raw {RawCount(original, Term)} ({original.Length} bytes); PDFium sees '{written.Item2}'");
        Log($"Q5 rewritten raw {RawCount(written.Item1, Term)} ({written.Item1.Length} bytes); prefix of the original kept: {written.Item1.AsSpan().StartsWith(original.AsSpan(0, 100))}");
        KeepForQpdf("q5-revisions-rewritten", written.Item1);

        // A page that is deleted takes its objects with it: page 1 of the everywhere fixture, whose own content (not the shared form) holds the term.
        var everywhere = TestData.Pdf("redact-everywhere.pdf");
        var deleted = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, everywhere, null);
                PdfiumPageEdit.Delete(document, 0);
                return Save(document);
            },
            cancellationToken: Ct);
        Assert.Equal((1, 1), RawCount(original, Term));
        Assert.Equal((0, 0), RawCount(written.Item1, Term)); // the older revision is not written again: a full rewrite leaves it out
        Assert.False(written.Item1.AsSpan().StartsWith(original.AsSpan(0, 100)));
        Assert.Equal("Publicline" + "staysvisible" + "Addressremoved", written.Item2);
        Log($"Q5 page one deleted: raw {RawCount(deleted, Term)} ({deleted.Length} bytes)");
        Assert.Equal((4, 6), RawCount(deleted, Term));
        foreach (var hit in Where(deleted, Term))
        {
            Log($"Q5   term in: {hit}");
        }
    }

    [Fact]
    public async Task ContentOutsidePageObjectsIsReportedOrFoundByTheRawScan()
    {
        var thread = PdfiumThread.Shared;
        using var folder = new TempFolder();
        var path = TestData.Pdf("redact-hidden.pdf");
        var original = await File.ReadAllBytesAsync(path, Ct);
        Log($"Q6 original raw {RawCount(original, Term)}");
        foreach (var hit in Where(original, Term))
        {
            Log($"Q6   original term in: {hit}");
        }

        var run = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, path, null);
                var letters = PageLetters(document, 0);
                using var page = PdfiumRedaction.OpenPage(document, 0);
                var objects = Enumerable.Range(0, page.CountObjects()).Select(page.GetObject).ToList();
                var types = objects.Select(o => page.Describe(o).Type).ToArray();

                // Remove everything the area would touch: the Type 3 text, the soft-masked rectangle, the shading.
                foreach (var obj in objects.Where(o => page.Describe(o).Type is not FpdfConstants.PageObjText || page.Describe(o).Top < 250))
                {
                    Assert.True(page.Remove(obj));
                    page.Destroy(obj);
                }

                Assert.True(page.GenerateContent());
                return (letters, types, Save(document), page.CountObjects());
            },
            cancellationToken: Ct);
        Log($"Q6 PDFium reads page text '{run.letters}' types=[{string.Join(",", run.types)}] objects left={run.Item4}");
        Log($"Q6 after removing the objects and writing the page again: raw {RawCount(run.Item3, Term)} ({run.Item3.Length} bytes)");
        foreach (var hit in Where(run.Item3, Term))
        {
            Log($"Q6   after removal term in: {hit}");
        }

        KeepForQpdf("q6-hidden-objects-removed", run.Item3);

        // Importing the page into a seed document does not drop the resources nothing uses any more.
        var seedPath = Path.Combine(folder.Path, "seed.pdf");
        await File.WriteAllBytesAsync(seedPath, SeedPdf(0), Ct);
        var resultPath = Path.Combine(folder.Path, "hidden-removed.pdf");
        await File.WriteAllBytesAsync(resultPath, run.Item3, Ct);
        var imported = await thread.InvokeAsync(
            () =>
            {
                using var seed = PdfiumDocument.Open(thread, seedPath, null);
                using var source = PdfiumDocument.Open(thread, resultPath, null);
                PdfiumPageEdit.Import(seed, source, [0], 0);
                return Save(seed);
            },
            cancellationToken: Ct);
        Log($"Q6 imported into a new document: raw {RawCount(imported, Term)} keys: {string.Join(" ", KeysIn(imported))}");

        // The term is in the glyph procedure of the Type 3 font and in the group of the soft mask; PDFium shows neither as page text.
        Assert.Equal("Hiddencontentfixtureaa", run.letters);
        Assert.Equal([FpdfConstants.PageObjText, FpdfConstants.PageObjText, FpdfConstants.PageObjPath, FpdfConstants.PageObjShading], run.types);
        Assert.Equal((2, 2), RawCount(original, Term));

        // Removing the objects the area would touch and writing the page again drops the soft mask but not the Type 3 font: the glyph
        // procedure with the term stays in the file (as a resource nothing uses), also in a document the page is imported into.
        Assert.Equal(2, run.Item4);
        Assert.Equal((0, 1), RawCount(run.Item3, Term));
        Assert.Contains(Where(run.Item3, Term), hit => hit.Contains("d1 BT", StringComparison.Ordinal));
        Assert.Equal((0, 1), RawCount(imported, Term));
        Assert.Equal(["/Type3"], KeysIn(imported));
        KeepForQpdf("q6-hidden-imported", imported);

        // The same for a form XObject and a tiling pattern that only this page uses (the second page of the everywhere fixture is deleted
        // first): the page objects are removed and the page written again.
        var everywhere = TestData.Pdf("redact-everywhere.pdf");
        var unused = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, everywhere, null);
                PdfiumPageEdit.Delete(document, 1);
                using var page = PdfiumRedaction.OpenPage(document, 0);
                var objects = Enumerable.Range(0, page.CountObjects()).Select(page.GetObject).ToList();
                foreach (var obj in objects.Where(o => page.Describe(o).Type is FpdfConstants.PageObjForm or FpdfConstants.PageObjPath))
                {
                    Assert.True(page.Remove(obj));
                    page.Destroy(obj);
                }

                Assert.True(page.GenerateContent());
                return Save(document);
            },
            cancellationToken: Ct);
        Log($"Q6 form and pattern of the only page removed: raw {RawCount(unused, Term)}");
        foreach (var hit in Where(unused, Term))
        {
            Log($"Q6   unused resource still holds the term: {hit}");
        }

        KeepForQpdf("q6-everywhere-form-pattern-removed", unused);

        // The form XObject (and the ExtGState of the soft mask above) is not written again when no object uses it; the tiling pattern and the
        // Type 3 font stay in the file although nothing uses them.
        Assert.DoesNotContain(Where(unused, Term), hit => hit.Contains("/BBox[ 0 0 150 30]", StringComparison.Ordinal));
        Assert.Contains(Where(unused, Term), hit => hit.Contains("/PatternType 1", StringComparison.Ordinal));
    }

    /// <summary>The inline image is lost by the page content generator; a new image object made from its pixels is not.</summary>
    [Fact]
    public async Task InlineImageSurvivesOnlyAsANewImageObject()
    {
        var thread = PdfiumThread.Shared;
        using var folder = new TempFolder();
        var engine = new PdfEngine();
        var path = TestData.Pdf("redact-image-kinds.pdf");
        var bytes = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, path, null);
                using var page = PdfiumRedaction.OpenPage(document, 0);
                var images = Enumerable.Range(0, page.CountObjects()).Where(i => page.Describe(page.GetObject(i)).Type == FpdfConstants.PageObjImage).ToList();
                var index = images[2];
                var inline = page.GetObject(index);
                var facts = ReadImageFacts(page, inline);
                var pixels = ReadImagePixels(page, inline, facts.Width, facts.Height)!.Value;
                Assert.True(page.TryGetMatrix(inline, out var matrix));
                var replacement = Fpdf.FPDFPageObj_NewImageObj(page.NativeDocument);
                Assert.NotEqual(0, replacement);
                Assert.True(WriteImagePixels(page, replacement, pixels.Width, pixels.Height, pixels.Bgra));
                Assert.True(page.SetMatrix(replacement, matrix));
                Assert.True(page.Remove(inline));
                page.Destroy(inline);
                Assert.True(page.Insert(replacement)); // no other object overlaps the image, so the place in the drawing order does not show
                Assert.True(page.GenerateContent());
                return Save(document);
            },
            cancellationToken: Ct);
        var resultPath = Path.Combine(folder.Path, "inline.pdf");
        await File.WriteAllBytesAsync(resultPath, bytes, Ct);
        KeepForQpdf("q3-inline-replaced", bytes);
        var before = await RedactionRender.RenderAsync(engine, path, 2);
        var after = await RedactionRender.RenderAsync(engine, resultPath, 2);
        var (different, compared, max) = CompareOutside(before, after, 2, [], 0);
        Log($"Q3 inline replaced by a new image object: page different={different} of {compared} max={max} box={DiffBox(before, after, 2)}");
        Assert.Equal(0, different);
    }

    [Fact]
    public async Task WhichObjectsDoesTheContentGeneratorDamage()
    {
        var thread = PdfiumThread.Shared;
        using var folder = new TempFolder();
        var engine = new PdfEngine();
        foreach (var name in new[] { "redact-everywhere.pdf", "redact-hidden.pdf", "redact-image-kinds.pdf", "redact-fonts.pdf", "vector-shapes.pdf" })
        {
            var path = TestData.Pdf(name);
            var before = await RedactionRender.RenderAsync(engine, path, 2);
            var count = await thread.InvokeAsync(
                () =>
                {
                    using var document = PdfiumDocument.Open(thread, path, null);
                    using var page = PdfiumRedaction.OpenPage(document, 0);
                    return page.CountObjects();
                },
                cancellationToken: Ct);
            for (var k = 0; k < count; k++)
            {
                var index = k;
                var (bytes, description) = await thread.InvokeAsync(
                    () =>
                    {
                        using var document = PdfiumDocument.Open(thread, path, null);
                        using var page = PdfiumRedaction.OpenPage(document, 0);
                        var obj = page.GetObject(index);
                        var info = page.Describe(obj);
                        Assert.True(page.Remove(obj));
                        Assert.True(page.InsertAt(obj, index));
                        Assert.True(page.GenerateContent());
                        return (Save(document), $"type={info.Type} bounds=({info.Left:0.#},{info.Bottom:0.#},{info.Right:0.#},{info.Top:0.#})");
                    },
                    cancellationToken: Ct);
                var resultPath = Path.Combine(folder.Path, $"{name}-{index}.pdf");
                await File.WriteAllBytesAsync(resultPath, bytes, Ct);
                var after = await RedactionRender.RenderAsync(engine, resultPath, 2);
                var (different, compared, max) = CompareOutside(before, after, 2, [], 0);
                Log($"OBJ {name} #{index} {description}: different={different} max={max}");
            }
        }
    }

    [Fact]
    public async Task SplittingTextObjectsKeepsTheRestOfTheGlyphs()
    {
        var thread = PdfiumThread.Shared;
        using var folder = new TempFolder();
        var engine = new PdfEngine();
        var path = TestData.Pdf("redact-fonts.pdf");

        var run = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, path, null);
                using var page = PdfiumRedaction.OpenPage(document, 0);
                var textPage = page.LoadTextPage();
                ProbeChar[] chars;
                try
                {
                    chars = ReadChars(textPage);
                }
                finally
                {
                    page.CloseTextPage(textPage);
                }

                // The line of a character: the baseline y of the first character of its text object.
                var lines = chars.Where(c => !c.Generated && c.TextObject != 0).GroupBy(c => c.TextObject).Select(g => g.ToList()).ToList();
                var rects = new List<ProbeRect>();
                ProbeRect? lineTwo = null;
                foreach (var line in lines)
                {
                    var found = FindTerm(line, Term);
                    var y = Math.Round(line[0].OriginY);
                    if (found is { } rect)
                    {
                        rects.Add(rect);
                        if (Math.Abs(y - 330) < 1)
                        {
                            lineTwo = rect;
                        }
                    }
                }

                // The line without ToUnicode (y = 270) has no readable text: it gets the rectangle of line two (same font and size) shifted down.
                rects.Add(lineTwo!.Value.Shift(0, -60));
                var decisions = DecomposeText(document, page, rects);
                Assert.True(page.GenerateContent());
                Verify(document, 0, decisions);
                var bytes = Save(document);
                return (rects, chars, decisions, bytes);
            },
            cancellationToken: Ct);

        foreach (var d in run.decisions)
        {
            Log("Q2 " + Describe(d));
        }

        var resultPath = Path.Combine(folder.Path, "fonts-result.pdf");
        await File.WriteAllBytesAsync(resultPath, run.bytes, Ct);
        KeepForQpdf("q2-fonts", run.bytes);

        var before = await RedactionRender.RenderAsync(engine, path, 2);
        var after = await RedactionRender.RenderAsync(engine, resultPath, 2);
        var (different, compared, max) = CompareOutside(before, after, 2, run.rects, 1.5);
        Log($"Q2 render outside: different={different} of {compared} max={max}");

        var afterLetters = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, resultPath, null);
                using var page = PdfiumRedaction.OpenPage(document, 0);
                var textPage = page.LoadTextPage();
                try
                {
                    var chars = ReadChars(textPage);
                    return chars.Where(c => !c.Generated && c.TextObject != 0).GroupBy(c => c.TextObject).Select(g => (Y: Math.Round(g.First().OriginY), Text: Letters(g))).ToList();
                }
                finally
                {
                    page.CloseTextPage(textPage);
                }
            },
            cancellationToken: Ct);
        foreach (var line in afterLetters)
        {
            Log($"Q2 after y={line.Y} '{line.Text}'");
        }

        var beforeLines = run.chars.Where(c => !c.Generated && c.TextObject != 0).GroupBy(c => c.TextObject).Select(g => (Y: Math.Round(g.First().OriginY), Text: Letters(g), Kept: Letters(g.Where(c => !run.rects.Any(r => r.Overlaps(c.Box)))))).ToList();
        foreach (var line in beforeLines)
        {
            Log($"Q2 before y={line.Y} '{line.Text}' expected '{line.Kept}'");
        }

        foreach (var line in run.chars.Where(c => !c.Generated && c.TextObject != 0).GroupBy(c => c.TextObject))
        {
            var band = line.Select(c => c.Box).Aggregate((x, y) => x.Union(y)).Inflate(3);
            var (d, n, m) = CompareOutside(before, after, 2, run.rects, 1.5, band);
            Log($"Q2 line y={Math.Round(line.First().OriginY)} outside-diff={d} of {n} max={m}");
        }

        // One object per line, in this order: Helvetica (standard font), simple TrueType, CID with ToUnicode (code = glyph id), CID with a
        // CIDToGIDMap stream and renumbered codes, CID without ToUnicode, embedded Type 1, TJ with kerning, Tc/Tw/Tz, turned by 30 degrees.
        Assert.Equal(9, run.decisions.Count);
        Assert.All(run.decisions, d => Assert.Equal(TextOutcome.Split, d.Outcome));

        // The read-back of the survivors (Unicode, origin, box) is right for seven of them ...
        Assert.Equal([true, true, true, false, false, true, true, true, true], run.decisions.Select(d => d.Verified == true).ToArray());

        // ... and that is exactly the set of lines that look the same outside the rectangle: PDFium takes the Unicode of the survivors for
        // the CID font with renumbered codes and for the font without ToUnicode and writes them as codes that show nothing (SetText says
        // true), so only the read-back or the render check shows it.
        var lineBands = run.chars.Where(c => !c.Generated && c.TextObject != 0).GroupBy(c => c.TextObject).Select(g => g.Select(c => c.Box).Aggregate((x, y) => x.Union(y)).Inflate(3)).ToList();
        for (var i = 0; i < lineBands.Count; i++)
        {
            var lineDifferent = CompareOutside(before, after, 2, run.rects, 1.5, lineBands[i]).Different;
            Assert.Equal(i is 3 or 4, lineDifferent > 0);
        }

        // The letters that remain on the verified lines are "Name:" and "ok" (the text of the term and the box of its characters is gone).
        Assert.Contains(afterLetters, l => l.Y == 390 && l.Text == "Name:");
        Assert.Contains(afterLetters, l => l.Y == 390 && l.Text == "ok");
        Assert.DoesNotContain(afterLetters, l => l.Text.Contains("Zephyr", StringComparison.Ordinal));
        Assert.DoesNotContain(afterLetters, l => l.Y is 300 or 270 && l.Text.Length > 0);
        Assert.True(different > 0);
    }
}

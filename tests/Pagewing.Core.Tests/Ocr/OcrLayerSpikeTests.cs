using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Redaction;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.Pdfium;
using Pagewing.Pdfium.Native;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Ocr;

/// <summary>
/// The spike of milestone 15 (Docs/DECISIONS.md, "OCR: Textebene, Spike (M15)"): how an invisible text layer is written with
/// PDFium and what it survives. The prototype <see cref="LayerSpike"/> is the variant that was chosen (Geist Regular loaded as a CID
/// font so PDFium writes a ToUnicode map, one text object for each word in render mode 3, fitted to the word box with the text
/// matrix, marked with the marked content tag <c>PagewingOcr</c>, in front of the page content); the assertions are the behaviour
/// found with PDFium 157.0.8100 and guard against an update that changes it. Set <c>PAGEWING_SPIKE_OUT</c> to a folder to keep the
/// sample files there (<c>ocr-layer-plain.pdf</c>, <c>ocr-layer-rotated.pdf</c>) for a look in other viewers.
/// </summary>
public sealed class OcrLayerSpikeTests
{
    private static readonly SpikeWord[] Words =
    [
        new("Müller", 72, 100, 160, 124),
        new("Straße", 180, 100, 262, 124),
        new("Größe", 282, 100, 360, 124),
    ];

    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Squeeze(string text) => string.Concat(text.Where(static c => !char.IsWhiteSpace(c)));

    [Fact]
    public async Task TextLayerIsExtractedWithUmlautsAndEszett()
    {
        using var folder = new TempFolder();
        var path = await WriteWithLayerAsync(folder, "plain", SpikePdf.Scan(1, 595, 842), 0, Words);

        var read = await ReadAsync(path, 0);
        Assert.Equal("MüllerStraßeGröße", Squeeze(read.Text));
        Assert.Equal(Words.Length, read.OwnObjects);
        KeepSample(path, "ocr-layer-plain.pdf");
    }

    [Fact]
    public async Task TextLayerDoesNotChangeTheRenderedPage()
    {
        using var folder = new TempFolder();
        var source = SpikePdf.Scan(1, 595, 842);
        var original = Path.Combine(folder.Path, "original.pdf");
        await File.WriteAllBytesAsync(original, source, Ct);
        var path = await WriteWithLayerAsync(folder, "render", source, 0, Words);

        await using var before = await engine.OpenAsync(original, cancellationToken: Ct);
        await using var after = await engine.OpenAsync(path, cancellationToken: Ct);
        var a = await before.RenderPageAsync(0, 1, cancellationToken: Ct);
        var b = await after.RenderPageAsync(0, 1, cancellationToken: Ct);
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        Assert.True(a.Pixels.Span.SequenceEqual(b.Pixels.Span), "the layer changed a pixel of the page");
    }

    [Fact]
    public async Task SelectionFollowsTheWordBox()
    {
        using var folder = new TempFolder();
        var path = await WriteWithLayerAsync(folder, "boxes", SpikePdf.Scan(1, 595, 842), 0, Words);
        var read = await ReadAsync(path, 0);
        AssertBoxes(read.Chars, Words);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task RotatedPageAndCropBoxKeepTheTextUpright(int rotate)
    {
        // A crop box that does not start at the origin and a page that is shown turned: the words are given in the displayed page.
        using var folder = new TempFolder();
        var source = SpikePdf.Scan(1, 595, 842, rotate, [20, 30, 520, 700]);
        var displayed = rotate is 90 or 270 ? (Width: 670.0, Height: 500.0) : (Width: 500.0, Height: 670.0);
        SpikeWord[] words = [new("Müller", 40, 60, 128, 84), new("Straße", 148, 60, 230, 84), new("Größe", 250, 200, 328, 224)];
        var path = await WriteWithLayerAsync(folder, "rotated" + rotate.ToString(CultureInfo.InvariantCulture), source, 0, words);

        var read = await ReadAsync(path, 0);
        Assert.Equal(displayed.Width, read.Width, 0.5);
        Assert.Equal(displayed.Height, read.Height, 0.5);
        AssertBoxes(read.Chars, words);
        if (rotate == 90)
        {
            KeepSample(path, "ocr-layer-rotated.pdf");
        }
    }

    [Fact]
    public async Task LayerSurvivesIncrementalSaveAndReopen()
    {
        using var folder = new TempFolder();
        var source = SpikePdf.Scan(1, 595, 842);
        var original = Path.Combine(folder.Path, "original.pdf");
        await File.WriteAllBytesAsync(original, source, Ct);

        var thread = PdfiumThread.Shared;
        var saved = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, original, null);
                LayerSpike.AddLayers(document, new Dictionary<int, IReadOnlyList<SpikeWord>> { [0] = Words });
                using var stream = new MemoryStream();
                document.SaveIncremental(stream);
                return stream.ToArray();
            },
            cancellationToken: Ct);

        Assert.True(saved.Length > source.Length);
        Assert.True(saved.AsSpan(0, source.Length).SequenceEqual(source), "the original bytes are not the prefix");
        var path = Path.Combine(folder.Path, "incremental.pdf");
        await File.WriteAllBytesAsync(path, saved, Ct);

        var read = await ReadAsync(path, 0);
        Assert.Equal(Words.Length, read.OwnObjects);
        Assert.Equal("MüllerStraßeGröße", Squeeze(read.Text));
        Assert.True(read.AllInvisible);
    }

    [Fact]
    public async Task LayerSurvivesPageImport()
    {
        // M13 builds the result from a copy of the file and imports pages with FPDF_ImportPagesByIndex.
        using var folder = new TempFolder();
        var withLayer = await WriteWithLayerAsync(folder, "source", SpikePdf.Scan(1, 595, 842), 0, Words);
        var target = Path.Combine(folder.Path, "target.pdf");
        await File.WriteAllBytesAsync(target, SpikePdf.Scan(1, 595, 842), Ct);

        var thread = PdfiumThread.Shared;
        var rewritten = await thread.InvokeAsync(
            () =>
            {
                using var destination = PdfiumDocument.Open(thread, target, null);
                using var source = PdfiumDocument.Open(thread, withLayer, null);
                PdfiumPageEdit.Import(destination, source, [0], 1);
                using var stream = new MemoryStream();
                PdfiumPageEdit.SaveRewrite(destination, stream);
                return stream.ToArray();
            },
            cancellationToken: Ct);

        var path = Path.Combine(folder.Path, "imported.pdf");
        await File.WriteAllBytesAsync(path, rewritten, Ct);
        var first = await ReadAsync(path, 0);
        var second = await ReadAsync(path, 1);
        Assert.Equal(0, first.OwnObjects);
        Assert.Equal(Words.Length, second.OwnObjects);
        Assert.Equal("MüllerStraßeGröße", Squeeze(second.Text));
    }

    [Fact]
    public async Task RedactingOneWordKeepsTheMarkOfTheOthers()
    {
        // M14: the rewrite splits and removes text objects; the others have to stay marked, the redacted word must be gone.
        using var folder = new TempFolder();
        var path = await WriteWithLayerAsync(folder, "redact", SpikePdf.Scan(1, 595, 842), 0, Words);
        var target = Path.Combine(folder.Path, "redacted.pdf");

        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var area = new PageRect(176, 96, 266, 128);
        var plan = RedactionPlan.Empty.With(new RedactionMark(Guid.NewGuid(), RedactionKind.Area, 0, [area], null, false));
        var result = await RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, target), null, Ct);
        Assert.True(result.Check.IsClean, RedactionRefusalText.Of(result.Check));

        var read = await ReadAsync(target, 0);
        Assert.Equal("MüllerGröße", Squeeze(read.Text));
        Assert.Equal(2, read.OwnObjects);
        Assert.False(RawBytesContain(await File.ReadAllBytesAsync(target, Ct), "Straße"));
    }

    [Fact]
    public async Task FileGrowsByFewKilobytesPerPage()
    {
        // The font is written once for the document, not for each page (one font handle for all pages of a run).
        using var folder = new TempFolder();
        var source = SpikePdf.Scan(3, 595, 842);
        var one = await WriteWithLayerAsync(folder, "one", source, 0, Words);
        var thread = PdfiumThread.Shared;
        var original = Path.Combine(folder.Path, "three.pdf");
        await File.WriteAllBytesAsync(original, source, Ct);
        var three = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, original, null);
                var all = new Dictionary<int, IReadOnlyList<SpikeWord>> { [0] = Words, [1] = Words, [2] = Words };
                LayerSpike.AddLayers(document, all);
                using var stream = new MemoryStream();
                document.SaveIncremental(stream);
                return stream.Length;
            },
            cancellationToken: Ct);

        var oneLength = new FileInfo(one).Length;
        var fontCost = oneLength - source.Length;
        var perPage = (three - oneLength) / 2;
        Assert.True(fontCost < 120_000, $"the first page costs {fontCost} bytes");
        Assert.True(perPage < 6_000, $"every further page costs {perPage} bytes");
    }

    [Fact]
    public async Task CharactersOutsideTheFontAreCounted()
    {
        using var folder = new TempFolder();
        SpikeWord[] words = [new("Привет", 72, 100, 160, 124), new("Größe", 180, 100, 260, 124)];
        var thread = PdfiumThread.Shared;
        var original = Path.Combine(folder.Path, "original.pdf");
        await File.WriteAllBytesAsync(original, SpikePdf.Scan(1, 595, 842), Ct);
        var counts = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, original, null);
                return LayerSpike.AddLayers(document, new Dictionary<int, IReadOnlyList<SpikeWord>> { [0] = words });
            },
            cancellationToken: Ct);

        Assert.Equal((1, 1), counts);
    }

    private static void AssertBoxes(IReadOnlyList<PdfiumChar> chars, IReadOnlyList<SpikeWord> words)
    {
        var own = chars.Where(static c => !c.Generated && !char.IsWhiteSpace((char)c.CodePoint)).ToArray();
        Assert.Equal(words.Sum(static w => w.Text.Length), own.Length);
        var index = 0;
        foreach (var word in words)
        {
            var part = own.Skip(index).Take(word.Text.Length).ToArray();
            index += word.Text.Length;
            Assert.Equal(word.Text, string.Concat(part.Select(static c => char.ConvertFromUtf32(c.CodePoint))));
            var tolerance = (word.Bottom - word.Top) / 2;
            Assert.InRange(part.Min(static c => c.Left), word.Left - tolerance, word.Left + tolerance);
            Assert.InRange(part.Max(static c => c.Right), word.Right - tolerance, word.Right + tolerance);
            Assert.InRange(part.Min(static c => c.Top), word.Top - tolerance, word.Bottom);
            Assert.InRange(part.Max(static c => c.Bottom), word.Top, word.Bottom + tolerance);
        }
    }

    private static bool RawBytesContain(byte[] file, string term)
    {
        // The layer is written as text in a content stream (compressed or not): look at the file and at its Latin-1 view alike.
        var latin = Encoding.Latin1.GetString(file);
        return latin.Contains(term, StringComparison.Ordinal) || Encoding.UTF8.GetString(file).Contains(term, StringComparison.Ordinal);
    }

    private static async Task<string> WriteWithLayerAsync(TempFolder folder, string name, byte[] source, int page, IReadOnlyList<SpikeWord> words)
    {
        var original = Path.Combine(folder.Path, name + "-source.pdf");
        await File.WriteAllBytesAsync(original, source, Ct);
        var thread = PdfiumThread.Shared;
        var bytes = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, original, null);
                LayerSpike.AddLayers(document, new Dictionary<int, IReadOnlyList<SpikeWord>> { [page] = words });
                using var stream = new MemoryStream();
                document.SaveIncremental(stream);
                return stream.ToArray();
            },
            cancellationToken: Ct);
        var path = Path.Combine(folder.Path, name + ".pdf");
        await File.WriteAllBytesAsync(path, bytes, Ct);
        return path;
    }

    private static async Task<LayerRead> ReadAsync(string path, int pageIndex)
    {
        var thread = PdfiumThread.Shared;
        return await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, path, null);
                using var page = document.LoadPage(pageIndex);
                using var text = page.LoadTextPage();
                var chars = PdfiumPageText.ReadChars(page, text, geometry: true, style: false);
                var invisible = PdfiumPageText.ReadInvisible(page, text, [(0, Math.Max(0, text.CharCount - 1))]);
                return new LayerRead(text.GetText(), chars, LayerSpike.CountOwn(page), invisible.Length > 0 && invisible[0], page.Width, page.Height);
            },
            cancellationToken: Ct);
    }

    private static void KeepSample(string path, string name)
    {
        var output = Environment.GetEnvironmentVariable("PAGEWING_SPIKE_OUT");
        if (!string.IsNullOrEmpty(output))
        {
            Directory.CreateDirectory(output);
            File.Copy(path, Path.Combine(output, name), overwrite: true);
        }
    }

    private sealed record LayerRead(string Text, PdfiumChar[] Chars, int OwnObjects, bool AllInvisible, double Width, double Height);
}

/// <summary>A word with its box in points from the top left of the displayed page.</summary>
internal readonly record struct SpikeWord(string Text, double Left, double Top, double Right, double Bottom);

/// <summary>The prototype of the text layer writer (the production code is <c>PdfiumOcrLayer</c>); runs on the PDFium thread.</summary>
internal static unsafe class LayerSpike
{
    /// <summary>The marked content tag of every text object of the layer.</summary>
    public const string Mark = "PagewingOcr";

    private const int TextObject = 1;

    /// <summary>Writes the words of the pages as invisible text; returns how many words were written and how many were left out (a character the font lacks).</summary>
    public static (int Written, int Skipped) AddLayers(PdfiumDocument document, IReadOnlyDictionary<int, IReadOnlyList<SpikeWord>> pages, bool marked = true)
    {
        var native = document.Access();
        var fontData = TextBoxFont.ReadData();
        nint font;
        fixed (byte* data = fontData)
        {
            font = Fpdf.FPDFText_LoadFont(native, data, (uint)fontData.Length, FpdfConstants.FontTrueType, cid: 1);
        }

        if (font == 0)
        {
            throw new InvalidOperationException("The font could not be loaded.");
        }

        var written = 0;
        var skipped = 0;
        try
        {
            foreach (var (index, words) in pages)
            {
                using var page = document.LoadPage(index);
                var handle = page.Access();
                if (!DisplayTransform.TryCreate(handle, page.Width, page.Height, out var transform))
                {
                    throw new InvalidOperationException("The page cannot be converted.");
                }

                foreach (var word in words)
                {
                    if (!word.Text.All(TextBoxCharacters.IsSupported))
                    {
                        skipped++;
                        continue;
                    }

                    Add(native, handle, font, transform, word, marked);
                    written++;
                }

                if (Fpdf.FPDFPage_GenerateContent(handle) == 0)
                {
                    throw new InvalidOperationException("The content could not be generated.");
                }
            }
        }
        finally
        {
            Fpdf.FPDFFont_Close(font);
        }

        return (written, skipped);
    }

    /// <summary>Counts the text objects of the page that carry the mark.</summary>
    public static int CountOwn(PdfiumPage page)
    {
        var handle = page.Access();
        var count = Fpdf.FPDFPage_CountObjects(handle);
        var own = 0;
        for (var i = 0; i < count; i++)
        {
            var item = Fpdf.FPDFPage_GetObject(handle, i);
            if (item != 0 && Fpdf.FPDFPageObj_GetType(item) == TextObject && IsOwn(item))
            {
                own++;
            }
        }

        return own;
    }

    private static bool IsOwn(nint item)
    {
        var marks = Fpdf.FPDFPageObj_CountMarks(item);
        for (var m = 0; m < marks; m++)
        {
            var mark = Fpdf.FPDFPageObj_GetMark(item, new CULong((uint)m));
            CULong length;
            if (mark == 0 || Fpdf.FPDFPageObjMark_GetName(mark, null, new CULong(0), &length) == 0 || length.Value is < 2 or > 64)
            {
                continue;
            }

            var buffer = new ushort[(int)length.Value / 2];
            fixed (ushort* name = buffer)
            {
                if (Fpdf.FPDFPageObjMark_GetName(mark, name, length, &length) != 0
                    && new string((char*)name, 0, (int)length.Value / 2).TrimEnd('\0') == Mark)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void Add(nint document, nint page, nint font, DisplayTransform transform, SpikeWord word, bool marked)
    {
        var height = word.Bottom - word.Top;
        var item = Fpdf.FPDFPageObj_CreateTextObj(document, font, (float)height);
        if (item == 0)
        {
            throw new InvalidOperationException("The text object could not be created.");
        }

        var text = (word.Text + "\0").Select(static c => (ushort)c).ToArray();
        fixed (ushort* characters = text)
        {
            if (Fpdf.FPDFText_SetText(item, characters) == 0)
            {
                Fpdf.FPDFPageObj_Destroy(item);
                throw new InvalidOperationException("The text could not be set.");
            }
        }

        float left, bottom, right, top;
        if (Fpdf.FPDFTextObj_SetTextRenderMode(item, FpdfConstants.TextRenderModeInvisible) == 0
            || Fpdf.FPDFPageObj_GetBounds(item, &left, &bottom, &right, &top) == 0
            || right - left <= 0)
        {
            Fpdf.FPDFPageObj_Destroy(item);
            throw new InvalidOperationException("The text object could not be prepared.");
        }

        // The text is wider or narrower than the box: stretch it horizontally; the baseline sits a fifth of the height above the bottom.
        var scale = (word.Right - word.Left) / (right - left);
        var origin = transform.ToUser(word.Left - (left * scale), word.Bottom - (height * 0.2));
        var along = transform.AlongX;
        var down = transform.AlongY;
        var matrix = new FsMatrix
        {
            A = (float)(along.X * scale),
            B = (float)(along.Y * scale),
            C = (float)-down.X,
            D = (float)-down.Y,
            E = (float)origin.X,
            F = (float)origin.Y,
        };
        var tag = Encoding.ASCII.GetBytes(Mark + "\0");
        fixed (byte* name = tag)
        {
            if (Fpdf.FPDFPageObj_SetMatrix(item, &matrix) == 0 || (marked && Fpdf.FPDFPageObj_AddMark(item, name) == 0))
            {
                Fpdf.FPDFPageObj_Destroy(item);
                throw new InvalidOperationException("The text object could not be placed.");
            }
        }

        if (Fpdf.FPDFPage_InsertObject(page, item) == 0)
        {
            throw new InvalidOperationException("The text object could not be inserted.");
        }
    }
}

/// <summary>Builds a small PDF of scan pages: one gray image over the whole media box, nothing else.</summary>
internal static class SpikePdf
{
    public static byte[] Scan(int pages, double width, double height, int rotate = 0, double[]? cropBox = null)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            string.Create(CultureInfo.InvariantCulture, $"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(0, pages).Select(static i => $"{4 + (2 * i)} 0 R"))}] /Count {pages} >>"),
        };

        const string Pixels = "00 40 80 C0 40 80 C0 FF 80 C0 FF 40 C0 FF 40 80>";
        objects.Add($"<< /Type /XObject /Subtype /Image /Width 4 /Height 4 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /ASCIIHexDecode /Length {Pixels.Length} >>\nstream\n{Pixels}\nendstream");
        for (var i = 0; i < pages; i++)
        {
            var crop = cropBox is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" /CropBox [{cropBox[0]} {cropBox[1]} {cropBox[2]} {cropBox[3]}]");
            var contents = 5 + (2 * i);
            objects.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}]{crop} /Rotate {rotate} /Resources << /XObject << /Im0 3 0 R >> >> /Contents {contents} 0 R >>"));
            var content = string.Create(CultureInfo.InvariantCulture, $"q {width} 0 0 {height} 0 0 cm /Im0 Do Q");
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }

        var builder = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(builder.Length);
            builder.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = builder.Length;
        builder.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        builder.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(builder.ToString());
    }
}

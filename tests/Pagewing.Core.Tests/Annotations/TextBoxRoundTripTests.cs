using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Text;
using Pagewing.Pdfium;
using Pagewing.TestSupport;
using Xunit;
using static Pagewing.Core.Tests.Annotations.AnnotationTestFiles;

namespace Pagewing.Core.Tests.Annotations;

/// <summary>Creating, replacing, saving and reading text boxes through <see cref="PdfDocument"/> (milestone 10, variant b2).</summary>
public sealed partial class TextBoxRoundTripTests : IDisposable
{
    private const double Scale = 3;

    private static readonly AnnotationColor TextBlue = new(30, 80, 220);

    private static readonly DateTimeOffset Created = new(2026, 10, 26, 8, 0, 0, TimeSpan.Zero);

    private readonly TempFolder folder = new();

    public TextBoxRoundTripTests() => UseTextBoxFont();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => folder.Dispose();

    [Fact]
    public async Task GermanUmlautsAndEuroSurvive()
    {
        const string Text = "Grüße aus Köln – 5 €";
        var spec = new TextBoxSpec(new PageRect(20, 20, 220, 50), Text, 12, TextBlue);
        string saved;
        PdfAnnotation created;
        await using (var document = await OpenCopyAsync("text-basic.pdf"))
        {
            created = await document.CreateTextBoxAsync(0, spec, "  Ada  ", Ct);
            Assert.Equal(AnnotationKind.TextBox, created.Kind);
            Assert.Equal("FreeText", created.Subtype);
            Assert.Equal(Text, created.Contents);
            Assert.Equal("Ada", created.Author);
            Assert.True(created.Editable);
            Assert.True(created.GeometryEditable);
            AssertSpec(spec, created.TextBox);
            saved = await SaveAsync(document, folder);
        }

        await using var reopened = await OpenAsync(saved);
        var annotation = Assert.Single(await reopened.GetAnnotationsAsync(0, Ct));
        Assert.Equal(created.Name, annotation.Name);
        Assert.Equal(AnnotationKind.TextBox, annotation.Kind);
        Assert.Equal(Text, annotation.Contents);
        Assert.True(annotation.GeometryEditable);
        AssertSpec(spec, annotation.TextBox);

        // The text is drawn in its colour inside the box.
        var image = await reopened.RenderPageAsync(0, Scale, cancellationToken: Ct);
        var ink = InkBounds(image, Scale, TextBlue, spec.Box.Inflate(10));
        Assert.NotNull(ink);
        AssertInside(spec.Box, ink.Value);
    }

    [Fact]
    public async Task LineBreakAndTwoSizesSurvive()
    {
        var small = new TextBoxSpec(new PageRect(20, 20, 200, 40), "Zeile eins\nZeile zwei", 12, TextBlue);
        var large = new TextBoxSpec(new PageRect(20, 100, 280, 120), "Erste\r\nZweite", 24, TextBlue);
        string saved;
        await using (var document = await OpenCopyAsync("text-basic.pdf"))
        {
            await document.CreateTextBoxAsync(0, small, null, Ct);
            await document.CreateTextBoxAsync(0, large, null, Ct);
            saved = await SaveAsync(document, folder);
        }

        await using var reopened = await OpenAsync(saved);
        var annotations = await reopened.GetAnnotationsAsync(0, Ct);
        Assert.Equal(2, annotations.Count);
        Assert.Equal("Zeile eins\nZeile zwei", annotations[0].TextBox!.Text);
        Assert.Equal("Erste\nZweite", annotations[1].TextBox!.Text);
        Assert.Equal([12d, 24d], annotations.Select(a => a.TextBox!.FontSize));

        // Both boxes were too low for two lines and grew downwards: padding, two lines of 1.3 em (Geist: ascent 1.005 + descent ~0.295).
        Assert.InRange(annotations[0].TextBox!.Box.Height, 8 + (2 * 12 * 1.29), 8 + (2 * 12 * 1.31));
        Assert.InRange(annotations[1].TextBox!.Box.Height, 8 + (2 * 24 * 1.29), 8 + (2 * 24 * 1.31));
        Assert.Equal(20, annotations[0].TextBox!.Box.Top, 2);

        // Two lines: the ink of the large box is taller than one line and its second line starts below the first.
        var image = await reopened.RenderPageAsync(0, Scale, cancellationToken: Ct);
        var ink = InkBounds(image, Scale, TextBlue, annotations[1].TextBox!.Box);
        Assert.NotNull(ink);
        Assert.True(ink.Value.Height > 1.3 * 24, $"The two lines of the large box are only {ink.Value.Height} pt high.");
    }

    [Fact]
    public async Task RotatedPageAndCropBox()
    {
        // Page 2 of drawings-foreign.pdf: /Rotate 90 and a CropBox offset; the text must stand upright on screen inside the box.
        var spec = new TextBoxSpec(new PageRect(40.5, 60.25, 230, 90), "Upright text", 16, TextBlue);
        string saved;
        await using (var document = await OpenCopyAsync(DrawingsForeign))
        {
            var created = await document.CreateTextBoxAsync(1, spec, null, Ct);
            AssertSpec(spec, created.TextBox);
            AssertRectNear(spec.Box, created.Bounds, 0.01);
            saved = await SaveAsync(document, folder);
        }

        await using var reopened = await OpenAsync(saved);
        var annotation = (await reopened.GetAnnotationsAsync(1, Ct)).Single(a => a.Kind == AnnotationKind.TextBox);
        Assert.True(annotation.GeometryEditable);
        AssertSpec(spec, annotation.TextBox);
        AssertRectNear(spec.Box, annotation.Bounds, 0.01);

        var image = await reopened.RenderPageAsync(1, Scale, cancellationToken: Ct);
        var ink = InkBounds(image, Scale, TextBlue, spec.Box.Inflate(10));
        Assert.NotNull(ink);
        AssertInside(spec.Box, ink.Value);

        // Upright: one line of text is far wider than high, starting at the left padding.
        Assert.True(ink.Value.Width > 4 * ink.Value.Height, $"The text is not upright: {ink.Value}.");
        Assert.InRange(ink.Value.Left, spec.Box.Left + TextBoxLayout.Padding - 1, spec.Box.Left + TextBoxLayout.Padding + 2);
        Assert.InRange(ink.Value.Top, spec.Box.Top + TextBoxLayout.Padding, spec.Box.Top + TextBoxLayout.Padding + (16 * 0.6));
    }

    [Fact]
    public async Task AppearanceIsAFormXObjectWithPrintFlag()
    {
        var copy = Copy("text-basic.pdf");
        var original = await File.ReadAllBytesAsync(copy, Ct);
        string saved;
        string name;
        await using (var document = await OpenAsync(copy))
        {
            name = (await document.CreateTextBoxAsync(0, new TextBoxSpec(new PageRect(20, 20, 220, 60), "Hello, Acrobat", 16, TextBlue), "Ada", Ct)).Name!;
            saved = await SaveAsync(document, folder);
        }

        var appended = Appended(original, await File.ReadAllBytesAsync(saved, Ct));
        var dictionary = AnnotationDictionary(appended, name);
        Assert.Matches(@"/Subtype\s*/FreeText", dictionary);
        Assert.Matches(@"/F\s*4(?![\d.])", dictionary);
        Assert.Matches(@"/Border\s*\[\s*0\s+0\s+0\s*\]", dictionary);
        Assert.Contains("/DA(/Helv 16 Tf 0.1176 0.3137 0.8627 rg)", dictionary, StringComparison.Ordinal);
        Assert.Contains("/DS(font: Helvetica,sans-serif 16.0pt; text-align:left; color:#1E50DC)", dictionary, StringComparison.Ordinal);
        Assert.Contains("/RC(", dictionary, StringComparison.Ordinal);
        Assert.Contains("/Contents(Hello, Acrobat)", dictionary, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"/C\s*\[", dictionary);
        Assert.DoesNotContain("/Popup", dictionary, StringComparison.Ordinal);
        Assert.Matches(@"/Subtype\s*/Form", AppearanceObject(appended, dictionary));

        // No font in the file: the glyphs are paths.
        Assert.DoesNotContain("/FontFile", appended, StringComparison.Ordinal);
        Assert.DoesNotContain("/Font", AppearanceObject(appended, dictionary), StringComparison.Ordinal);

        // The appearance starts with Pagewing's marker and holds only paths.
        var appearance = await AppearanceTextAsync(saved, 0, 0);
        Assert.StartsWith(PdfiumTextBoxes.Marker, appearance, StringComparison.Ordinal);
        Assert.DoesNotContain(" Tj", appearance, StringComparison.Ordinal);
        Assert.Contains(" cm ", appearance, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EditingTheTextReplacesTheAppearance()
    {
        await using var document = await OpenCopyAsync("text-basic.pdf");
        document.Clock = new FixedClock(Created);
        var created = await document.CreateTextBoxAsync(0, new TextBoxSpec(new PageRect(20, 20, 220, 50), "Before", 12, TextBlue), "Ada", Ct);
        var before = await AppearanceTextAsync(document, 0);

        var changed = await document.ReplaceTextBoxAsync(LocatorOf(created), created.TextBox! with { Text = "After the change", FontSize = 16 }, Ct);

        var listed = Assert.Single(await document.GetAnnotationsAsync(0, Ct));
        Assert.Equal(created.Name, changed.Name);
        Assert.Equal(created.Name, listed.Name);
        Assert.Equal("Ada", listed.Author);
        Assert.Equal("After the change", listed.Contents);
        Assert.Equal(16, listed.TextBox!.FontSize);
        Assert.True(listed.GeometryEditable);
        var after = await AppearanceTextAsync(document, 0);
        Assert.NotEqual(before, after);
        Assert.StartsWith(PdfiumTextBoxes.Marker, after, StringComparison.Ordinal);

        // The comment alone changes the text too, and the colour alone recolours it.
        var retyped = await document.UpdateAnnotationAsync(LocatorOf(listed), new AnnotationEdit(Contents: "Third"), Ct);
        Assert.Equal("Third", retyped.TextBox!.Text);
        var recoloured = await document.UpdateAnnotationAsync(LocatorOf(retyped), new AnnotationEdit(Color: new AnnotationColor(220, 30, 30)), Ct);
        Assert.Equal(new AnnotationColor(220, 30, 30), recoloured.TextBox!.Color);
        Assert.Equal("Third", recoloured.TextBox!.Text);
        Assert.True(recoloured.GeometryEditable);
        Assert.Single(await document.GetAnnotationsAsync(0, Ct));
    }

    [Fact]
    public async Task MovingKeepsTheNameAuthorAndCreationDate()
    {
        var copy = Copy("text-basic.pdf");
        var original = await File.ReadAllBytesAsync(copy, Ct);
        string saved;
        PdfAnnotation moved;
        await using (var document = await OpenAsync(copy))
        {
            document.Clock = new FixedClock(Created);
            var created = await document.CreateTextBoxAsync(0, new TextBoxSpec(new PageRect(20, 20, 220, 50), "Move me", 12, TextBlue), "Ada", Ct);
            document.Clock = new FixedClock(Created.AddHours(1));

            var target = new PageRect(60, 120, 260, 150);
            moved = await document.ReplaceTextBoxAsync(LocatorOf(created), created.TextBox! with { Box = target }, Ct);

            Assert.Equal(created.Name, moved.Name);
            Assert.Equal("Ada", moved.Author);
            AssertRectNear(target, moved.TextBox!.Box, 0.01);
            Assert.Equal(Created.AddHours(1), moved.Modified);
            Assert.Single(await document.GetAnnotationsAsync(0, Ct));
            saved = await SaveAsync(document, folder);
        }

        var dictionary = AnnotationDictionary(Appended(original, await File.ReadAllBytesAsync(saved, Ct)), moved.Name!);
        Assert.Contains("/CreationDate(D:20261026080000Z)", dictionary, StringComparison.Ordinal);
        Assert.Contains("/M(D:20261026090000Z)", dictionary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBoxGrowsToFitButNotBeyondThePage()
    {
        await using var document = await OpenCopyAsync("text-basic.pdf");
        var size = await document.GetPageSizeAsync(0, Ct);
        var lines = string.Join('\n', Enumerable.Repeat("line", 60));

        var created = await document.CreateTextBoxAsync(0, new TextBoxSpec(new PageRect(20, 20, 120, 30), lines, 12, TextBlue), null, Ct);

        Assert.Equal(20, created.TextBox!.Box.Top, 2);
        Assert.Equal(size.Height, created.TextBox.Box.Bottom, 1);

        // A box higher than its text keeps its height.
        var tall = await document.CreateTextBoxAsync(0, new TextBoxSpec(new PageRect(150, 20, 250, 120), "short", 12, TextBlue), null, Ct);
        AssertRectNear(new PageRect(150, 20, 250, 120), tall.TextBox!.Box, 0.01);
    }

    [Fact]
    public async Task AcrobatEditedAppearanceLocksTheBox()
    {
        // Pagewing's marker with the fingerprint of the text, size and colour and the box: own. The same marker after the
        // text changed (as when another program edits /Contents) or an appearance without the marker (Acrobat writes its own
        // when the text is edited there): locked.
        const string Rect = "20 200 220 260";
        var own = Marker("Hello", "12", (30, 80, 220), 20, 200, 220, 260);
        var path = WritePdf(folder, "edited.pdf",
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [4 0 R 6 0 R 8 0 R] >>",
            FreeText("Hello", Rect, 5, "own"),
            Form(Rect, own + "q 0 0 1 rg 30 210 100 20 re f Q"),
            FreeText("Hello again", Rect, 7, "text-changed"),
            Form(Rect, own + "q 0 0 1 rg 30 210 100 20 re f Q"),
            FreeText("Hello", Rect, 9, "acrobat"),
            Form(Rect, "q BT /Helv 12 Tf 0.1176 0.3137 0.8627 rg 22 240 Td (Hello) Tj ET Q"),
        ]);

        await using var document = await OpenAsync(path);
        var annotations = await document.GetAnnotationsAsync(0, Ct);

        Assert.Equal(["own", "text-changed", "acrobat"], annotations.Select(a => a.Name));
        Assert.All(annotations, a => Assert.Equal(AnnotationKind.TextBox, a.Kind));
        Assert.All(annotations, a => Assert.True(a.Editable));
        Assert.Equal([true, false, false], annotations.Select(a => a.GeometryEditable));
        Assert.Equal("Hello again", annotations[1].Contents);
        Assert.Equal(TextBlue, annotations[2].TextBox!.Color);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            document.ReplaceTextBoxAsync(LocatorOf(annotations[2]), annotations[2].TextBox! with { Text = "Changed" }, Ct));
    }

    [Fact]
    public async Task ForeignFreeTextIsLocked()
    {
        var path = WritePdf(folder, "foreign.pdf",
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [4 0 R 5 0 R] >>",
            "<< /Type /Annot /Subtype /FreeText /Rect [20 200 220 260] /Contents (Foreign) /DA (/Helv 14 Tf 1 0 0 rg) /NM (foreign) /F 4 >>",
            "<< /Type /Annot /Subtype /FreeText /Rect [20 100 220 160] /Contents (No style) /NM (plain) >>",
        ]);
        await using var document = await OpenAsync(path);
        var annotations = await document.GetAnnotationsAsync(0, Ct);

        Assert.All(annotations, a => Assert.Equal(AnnotationKind.TextBox, a.Kind));
        Assert.All(annotations, a => Assert.False(a.GeometryEditable));
        Assert.Equal(14, annotations[0].TextBox!.FontSize);
        Assert.Equal(new AnnotationColor(255, 0, 0), annotations[0].TextBox!.Color);
        Assert.Equal(TextBoxSpec.DefaultFontSize, annotations[1].TextBox!.FontSize);
        Assert.Equal(new AnnotationColor(0, 0, 0), annotations[1].TextBox!.Color);
        AssertRectNear(new PageRect(20, 40, 220, 100), annotations[0].Bounds, 0.01);

        // Neither the box nor the text (which is what the box shows) can be changed; it can be deleted.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            document.ReplaceTextBoxAsync(LocatorOf(annotations[0]), annotations[0].TextBox! with { Box = new PageRect(0, 0, 100, 40) }, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            document.UpdateAnnotationAsync(LocatorOf(annotations[0]), new AnnotationEdit(Contents: "Other"), Ct));
        await document.DeleteAnnotationAsync(LocatorOf(annotations[0]), Ct);
        Assert.Equal(["plain"], (await document.GetAnnotationsAsync(0, Ct)).Select(a => a.Name));
    }

    [Fact]
    public async Task HostileTextIsRefused()
    {
        await using var document = await OpenCopyAsync("text-basic.pdf");
        var box = new PageRect(20, 20, 220, 60);

        await Assert.ThrowsAsync<ArgumentException>(() => document.CreateTextBoxAsync(0, new TextBoxSpec(box, "Ωmega", 12, TextBlue), null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => document.CreateTextBoxAsync(0, new TextBoxSpec(box, "a😀", 12, TextBlue), null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => document.CreateTextBoxAsync(0, new TextBoxSpec(box, new string('a', TextBoxSpec.MaxTextLength + 1), 12, TextBlue), null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => document.CreateTextBoxAsync(0, new TextBoxSpec(box, " \n ", 12, TextBlue), null, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.CreateTextBoxAsync(0, new TextBoxSpec(box, "a", 200, TextBlue), null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => document.CreateTextBoxAsync(0, new TextBoxSpec(new PageRect(20, 20, 22, 60), "a", 12, TextBlue), null, Ct));
        Assert.Empty(await document.GetAnnotationsAsync(0, Ct));

        // The longest text that is allowed fits within the appearance limit.
        var longest = await document.CreateTextBoxAsync(0, new TextBoxSpec(new PageRect(10, 10, 600, 20), LongText(TextBoxSpec.MaxTextLength), 6, TextBlue), null, Ct);
        Assert.Equal(TextBoxSpec.MaxTextLength, longest.TextBox!.Text.Length);
        Assert.True(longest.GeometryEditable);
    }

    [Fact]
    public async Task ExistingBytesStayThePrefix()
    {
        var copy = Copy(DrawingsForeign);
        var original = await File.ReadAllBytesAsync(copy, Ct);
        string saved;
        await using (var document = await OpenAsync(copy))
        {
            await document.CreateTextBoxAsync(0, new TextBoxSpec(new PageRect(20, 20, 220, 50), "Prefix", 12, TextBlue), null, Ct);
            saved = await SaveAsync(document, folder);
        }

        _ = Appended(original, await File.ReadAllBytesAsync(saved, Ct));
    }

    /// <summary>A text of every supported character, repeated, with a line feed now and then.</summary>
    internal static string LongText(int length)
    {
        var builder = new StringBuilder(length);
        var all = TextBoxCharacters.All;
        for (var i = 0; builder.Length < length; i++)
        {
            builder.Append(i % 80 == 79 ? '\n' : all[i % all.Count]);
        }

        return builder.ToString();
    }

    private static string Marker(string text, string size, (byte R, byte G, byte B) color, double l, double b, double r, double t) =>
        string.Create(CultureInfo.InvariantCulture, $"{PdfiumTextBoxes.Marker}{PdfiumTextBoxes.Fingerprint(text, size, color)} {l} {b} {r} {t}\n");

    private static string FreeText(string contents, string rect, int appearance, string name) =>
        $"<< /Type /Annot /Subtype /FreeText /Rect [{rect}] /Contents ({contents}) /DA (/Helv 12 Tf 0.1176 0.3137 0.8627 rg) /NM ({name}) /F 4 /AP << /N {appearance} 0 R >> >>";

    private static string Form(string rect, string content) =>
        $"<< /Type /XObject /Subtype /Form /BBox [{rect}] /Length {content.Length} >>\nstream\n{content}\nendstream";

    private static AnnotationLocator LocatorOf(PdfAnnotation annotation) => new(annotation.Page, annotation.Index, annotation.Name);

    private static void AssertSpec(TextBoxSpec expected, TextBoxSpec? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(TextBoxSpec.NormalizeText(expected.Text), actual.Text);
        Assert.Equal(expected.FontSize, actual.FontSize);
        Assert.Equal(expected.Color, actual.Color);
        Assert.Equal(expected.Box.Left, actual.Box.Left, 2);
        Assert.Equal(expected.Box.Top, actual.Box.Top, 2);
        Assert.Equal(expected.Box.Right, actual.Box.Right, 2);
        Assert.True(actual.Box.Bottom >= expected.Box.Bottom - 0.01, $"The box shrank: {actual.Box}.");
    }

    private static void AssertInside(PageRect box, PageRect ink) =>
        Assert.True(
            ink.Left >= box.Left && ink.Top >= box.Top && ink.Right <= box.Right && ink.Bottom <= box.Bottom,
            $"The text {ink} reaches out of its box {box}.");

    /// <summary>The bounds, in page points, of the pixels close to <paramref name="color"/>, within <paramref name="within"/> if given.</summary>
    internal static PageRect? InkBounds(RenderedImage image, double scale, AnnotationColor color, PageRect? within = null)
    {
        var pixels = image.Pixels.Span;
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                if (within is { } area && !area.Contains((x + 0.5) / scale, (y + 0.5) / scale))
                {
                    continue;
                }

                var o = (y * image.Stride) + (x * 4);
                if (Math.Abs(pixels[o] - color.B) + Math.Abs(pixels[o + 1] - color.G) + Math.Abs(pixels[o + 2] - color.R) <= 60)
                {
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x + 1);
                    bottom = Math.Max(bottom, y + 1);
                }
            }
        }

        return left == int.MaxValue ? null : new PageRect(left / scale, top / scale, right / scale, bottom / scale);
    }

    private static Task<string> AppearanceTextAsync(string path, int pageIndex, int index) =>
        PdfiumThread.Shared.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(PdfiumThread.Shared, path, null);
                using var page = document.LoadPage(pageIndex);
                return AppearanceOf(page, index);
            },
            cancellationToken: Ct);

    private async Task<string> AppearanceTextAsync(PdfDocument document, int pageIndex)
    {
        var path = await SaveAsync(document, folder, "appearance-" + Guid.NewGuid().ToString("N") + ".pdf");
        return await AppearanceTextAsync(path, pageIndex, 0);
    }

    private static string AppearanceOf(PdfiumPage page, int index)
    {
        var annotation = Pagewing.Pdfium.Native.Fpdf.FPDFPage_GetAnnot(page.Access(), index);
        char[]? scratch = null;
        try
        {
            return PdfiumTextBoxes.ReadAppearance(annotation, ref scratch).ToString();
        }
        finally
        {
            Pagewing.Pdfium.Native.Fpdf.FPDFPage_CloseAnnot(annotation);
        }
    }

    private static string Appended(byte[] original, byte[] saved)
    {
        Assert.True(saved.Length > original.Length);
        Assert.True(saved.AsSpan(0, original.Length).SequenceEqual(original), "The original bytes were changed.");
        return Encoding.Latin1.GetString(saved, original.Length, saved.Length - original.Length);
    }

    private static string AnnotationDictionary(string appended, string name)
    {
        var match = ObjectPattern().Matches(appended).LastOrDefault(m => m.Groups["body"].Value.Contains("/NM(" + name + ")", StringComparison.Ordinal));
        Assert.True(match is not null, $"No indirect annotation object with /NM ({name}) in the appended revision.");
        return match.Groups["body"].Value;
    }

    private static string AppearanceObject(string appended, string dictionary)
    {
        var reference = AppearancePattern().Match(dictionary);
        Assert.True(reference.Success, "The annotation has no /AP << /N n 0 R >>: " + dictionary);
        var number = reference.Groups["n"].Value;
        var match = ObjectPattern().Matches(appended).LastOrDefault(m => m.Groups["number"].Value == number);
        Assert.True(match is not null, $"The appearance object {number} is not in the appended revision.");
        var body = match.Groups["body"].Value;
        var stream = body.IndexOf("stream", StringComparison.Ordinal);
        return stream < 0 ? body : body[..stream];
    }

    [GeneratedRegex(@"(?<!\d)(?<number>\d+)\s+0\s+obj\b(?<body>.*?)\bendobj", RegexOptions.Singleline)]
    private static partial Regex ObjectPattern();

    [GeneratedRegex(@"/AP\s*<<\s*/N\s+(?<n>\d+)\s+0\s+R\s*>>")]
    private static partial Regex AppearancePattern();

    private string Copy(string name)
    {
        var path = Path.Combine(folder.Path, "copy-" + name);
        File.Copy(TestData.Pdf(name), path);
        return path;
    }

    private Task<PdfDocument> OpenCopyAsync(string name) => OpenAsync(Copy(name));
}

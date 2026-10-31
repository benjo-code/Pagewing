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
        // Pagewing's marker with the fingerprint of the text, size and colour and the box, /DS and /RC as Pagewing writes
        // them, flag Print only and none of the keys it never writes: own. Anything another program changed without
        // rewriting the appearance (the text, the box, a flag, /DS, /RC, an added key) or an appearance without the marker
        // (Acrobat writes its own when the text is edited there): locked.
        const string Rect = "20 200 220 260";
        var marker = Marker("Hello", "12", (30, 80, 220), 20, 200, 220, 260);
        var cases = new (string Name, string Contents, string AppearanceContent, string Extra, string RectOf, bool Own)[]
        {
            ("own", "Hello", marker, string.Empty, Rect, true),
            ("text-changed", "Hello again", marker, string.Empty, Rect, false),
            ("acrobat", "Hello", string.Empty, string.Empty, Rect, false),
            ("moved", "Hello", marker, string.Empty, "60 200 260 260", false),
            ("other-flag", "Hello", marker, "/F 36", Rect, false),
            ("ds-changed", "Hello", marker, "/DS (font: Helvetica 14pt)", Rect, false),
            ("rc-changed", "Hello", marker, "/RC (<body><p>Other</p></body>)", Rect, false),
            ("key-C", "Hello", marker, "/C [1 0 0]", Rect, false),
            ("key-IC", "Hello", marker, "/IC [1 0 0]", Rect, false),
            ("key-CA", "Hello", marker, "/CA 0.5", Rect, false),
            ("key-BS", "Hello", marker, "/BS << /W 2 >>", Rect, false),
            ("key-BE", "Hello", marker, "/BE << /S /C /I 1 >>", Rect, false),
            ("key-RD", "Hello", marker, "/RD [1 1 1 1]", Rect, false),
            ("key-IT", "Hello", marker, "/IT /FreeTextCallout", Rect, false),
            ("key-CL", "Hello", marker, "/CL [10 10 20 20]", Rect, false),
            ("key-LE", "Hello", marker, "/LE /OpenArrow", Rect, false),
        };
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", string.Empty };
        var annots = new List<string>();
        foreach (var (name, contents, appearance, extra, rect, _) in cases)
        {
            annots.Add(string.Create(CultureInfo.InvariantCulture, $"{objects.Count + 1} 0 R"));
            var content = appearance.Length > 0
                ? appearance + "q 0 0 1 rg 30 210 100 20 re f Q"
                : "q BT /Helv 12 Tf 0.1176 0.3137 0.8627 rg 22 240 Td (Hello) Tj ET Q";
            objects.Add(OwnFreeText(contents, rect, objects.Count + 2, name, extra));
            objects.Add(Form(Rect, content));
        }

        objects[2] = $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [{string.Join(' ', annots)}] >>";
        var path = WritePdf(folder, "edited.pdf", objects);

        await using var document = await OpenAsync(path);
        var annotations = await document.GetAnnotationsAsync(0, Ct);

        Assert.Equal(cases.Select(c => c.Name), annotations.Select(a => a.Name));
        Assert.All(annotations, a => Assert.Equal(AnnotationKind.TextBox, a.Kind));
        Assert.All(annotations, a => Assert.True(a.Editable));
        foreach (var (expected, annotation) in cases.Zip(annotations))
        {
            Assert.True(expected.Own == annotation.GeometryEditable, $"{expected.Name}: own {annotation.GeometryEditable}, expected {expected.Own}.");
        }

        Assert.Equal("Hello again", annotations[1].Contents);
        Assert.Equal(TextBlue, annotations[2].TextBox!.Color);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            document.ReplaceTextBoxAsync(LocatorOf(annotations[2]), annotations[2].TextBox! with { Text = "Changed" }, Ct));
    }

    [Fact]
    public async Task AppearanceReadsOfAHostilePageStayWithinTheBudget()
    {
        // An own text box first, then 300 FreeTexts that look own up to the appearance and share one appearance of 600,000
        // characters (1.2 MB as PDFium reports it). The listing decodes appearances only within its budget: the own box is
        // still recognised, the others count as foreign, and at most budget / 1.2 MB + 1 appearances are asked for.
        const string Rect = "20 200 220 260";
        var hostileContent = new StringBuilder(PdfiumTextBoxes.Marker).Append(new string('0', 32)).Append(" 20 200 220 260\n");
        while (hostileContent.Length < 600_000)
        {
            hostileContent.Append("q 0 0 1 rg 30 210 100 20 re f Q\n");
        }

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            string.Empty,
            OwnFreeText("Hello", Rect, 5, "own", string.Empty),
            Form(Rect, Marker("Hello", "12", (30, 80, 220), 20, 200, 220, 260) + "q 0 0 1 rg 30 210 100 20 re f Q"),
            Form(Rect, hostileContent.ToString()),
        };
        var annots = new List<string> { "4 0 R" };
        for (var i = 0; i < 300; i++)
        {
            annots.Add(string.Create(CultureInfo.InvariantCulture, $"{objects.Count + 1} 0 R"));
            objects.Add(OwnFreeText("Hello", Rect, 6, "hostile-" + i, string.Empty));
        }

        objects[2] = $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [{string.Join(' ', annots)}] >>";
        var path = WritePdf(folder, "hostile.pdf", objects);

        // The read of the page as the listing does it, counting what was charged.
        var (own, foreign, charged) = await PdfiumThread.Shared.InvokeAsync(
            () =>
            {
                using var pdf = PdfiumDocument.Open(PdfiumThread.Shared, path, null);
                using var page = pdf.LoadPage(0);
                Assert.True(DisplayTransform.TryCreate(page.Access(), page.Width, page.Height, out var transform));
                var budget = PdfiumTextBoxes.MaxAppearanceBytesPerPage;
                char[]? scratch = null;
                var (ownCount, foreignCount) = (0, 0);
                for (var i = 0; i < annots.Count; i++)
                {
                    var box = PdfiumTextBoxes.Read(page, i, transform, ref budget, ref scratch, out _);
                    _ = box!.IsOwn ? ownCount++ : foreignCount++;
                }

                PdfiumTextBoxes.ReturnScratch(ref scratch);
                return (ownCount, foreignCount, PdfiumTextBoxes.MaxAppearanceBytesPerPage - budget);
            },
            cancellationToken: Ct);

        Assert.Equal(1, own);
        Assert.Equal(300, foreign);
        var hostileBytes = (long)(hostileContent.Length + 1) * 2;
        Assert.True(charged <= PdfiumTextBoxes.MaxAppearanceBytesPerPage + hostileBytes, $"{charged} bytes charged");
        Assert.True(charged / hostileBytes <= (PdfiumTextBoxes.MaxAppearanceBytesPerPage / hostileBytes) + 2, $"{charged / hostileBytes} appearances read");

        // The document listing reads the page the same way and still lists the own box as own.
        await using var document = await OpenAsync(path);
        var annotations = await document.GetAnnotationsAsync(0, Ct);
        Assert.True(annotations[0].GeometryEditable);
        Assert.All(annotations.Skip(1), a => Assert.False(a.GeometryEditable));
    }

    /// <summary>A FreeText as Pagewing writes it (text "Hello"-like, 12 pt, blue), with <paramref name="extra"/> keys.</summary>
    private static string OwnFreeText(string contents, string rect, int appearance, string name, string extra)
    {
        (byte, byte, byte) blue = (30, 80, 220);

        // An extra /DS, /RC or /F takes the place of the one Pagewing writes (a dictionary has each key once).
        var ds = extra.Contains("/DS ", StringComparison.Ordinal) ? string.Empty : $"/DS ({PdfiumTextBoxes.DefaultStyle(12, blue)}) ";
        var rc = extra.Contains("/RC ", StringComparison.Ordinal) ? string.Empty : $"/RC ({PdfiumTextBoxes.RichText(contents, 12, blue)}) ";
        var flags = extra.Contains("/F ", StringComparison.Ordinal) ? string.Empty : "/F 4 ";
        return $"<< /Type /Annot /Subtype /FreeText /Rect [{rect}] /Contents ({contents}) /DA ({PdfiumTextBoxes.DefaultAppearance("12", blue)}) "
            + $"{ds}{rc}/NM ({name}) {flags}{extra} /AP << /N {appearance} 0 R >> >>";
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
            var budget = long.MaxValue;
            return PdfiumTextBoxes.ReadAppearance(annotation, ref scratch, ref budget).ToString();
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

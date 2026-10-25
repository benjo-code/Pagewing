using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;
using static Pagewing.Core.Tests.Annotations.AnnotationTestFiles;

namespace Pagewing.Core.Tests.Annotations;

/// <summary>
/// Writes the samples for the check in other viewers (Chrome, Acrobat Reader, pdf.js, Okular …; <c>packaging/README.md</c>,
/// "Annotationen im Fremdviewer prüfen"): every source file next to a copy annotated by Pagewing's own pipeline
/// (<see cref="AnnotationEditor"/> over a <see cref="ViewerSession"/>, saved through the editor and
/// <c>DocumentSaver</c>), plus <c>README-interop.txt</c> with what to look for. Runs only with
/// <c>PAGEWING_INTEROP_OUT=&lt;folder&gt;</c>; nothing is written anywhere else (the work happens in a temp folder).
/// </summary>
public sealed partial class InteropSampleTests
{
    /// <summary>The environment variable that names the output folder.</summary>
    public const string OutputVariable = "PAGEWING_INTEROP_OUT";

    /// <summary>User password of the encrypted PDFium test files (see <c>testdata/pdfs/LICENSES.md</c>).</summary>
    private const string UserPassword = "hôtel";

    private const string Author = "Pagewing Interop";

    /// <summary>The fixed clock of every change, so the samples differ only in their random <c>/NM</c>.</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The drawing samples of milestone 10: all five kinds on a plain page, next to the annotations of other programs, on a
    /// rotated page, inside a CropBox, moved and recoloured. Positions are fractions of the displayed page (origin top left).
    /// </summary>
    private static IReadOnlyList<Sample> DrawingSamples { get; } =
    [
        new("text-basic.pdf", null, "Page 1: every drawing kind once (the arrow twice: one thick, one thin across the page); page 2: a thick freehand stroke and a thin rectangle with a comment.",
            [],
            Name: "drawings-text-basic",
            Draws:
            [
                new(0, AnnotationKind.Ink, 0.10, 0.40, 0.45, 0.55, DrawColor.Red, StrokeWidth.Medium),
                new(0, AnnotationKind.Rectangle, 0.55, 0.40, 0.90, 0.55, DrawColor.Blue, StrokeWidth.Thick),
                new(0, AnnotationKind.Ellipse, 0.10, 0.60, 0.45, 0.75, DrawColor.Green, StrokeWidth.Medium, "Interop: green ellipse"),
                new(0, AnnotationKind.Line, 0.55, 0.62, 0.90, 0.75, DrawColor.Black, StrokeWidth.Thin),
                new(0, AnnotationKind.Arrow, 0.10, 0.80, 0.50, 0.90, DrawColor.Orange, StrokeWidth.Thick),
                new(0, AnnotationKind.Arrow, 0.60, 0.30, 0.97, 0.05, DrawColor.Black, StrokeWidth.Thin, "Interop: thin arrow to the top right corner"),
                new(1, AnnotationKind.Ink, 0.10, 0.20, 0.80, 0.45, DrawColor.Blue, StrokeWidth.Thick),
                new(1, AnnotationKind.Rectangle, 0.20, 0.55, 0.70, 0.80, DrawColor.Orange, StrokeWidth.Thin, "Interop: thin orange rectangle"),
            ]),
        new("annotations.pdf", null, "Annotations of another program (highlight, link and more) stay as they are; four drawings next to them.",
            [],
            Name: "drawings-annotations",
            Draws:
            [
                new(0, AnnotationKind.Rectangle, 0.10, 0.50, 0.40, 0.60, DrawColor.Red, StrokeWidth.Medium),
                new(0, AnnotationKind.Arrow, 0.50, 0.55, 0.90, 0.65, DrawColor.Blue, StrokeWidth.Medium),
                new(0, AnnotationKind.Ink, 0.10, 0.70, 0.45, 0.80, DrawColor.Green, StrokeWidth.Medium),
                new(0, AnnotationKind.Line, 0.55, 0.72, 0.90, 0.85, DrawColor.Black, StrokeWidth.Medium),
            ]),
        new("rotated-page.pdf", null, "The page has /Rotate 90; the drawings are placed as the page looks on screen.",
            [],
            Name: "drawings-rotated-page",
            Draws: FiveKinds()),
        new("cropbox.pdf", null, "The CropBox is smaller than the MediaBox and offset; the drawings are placed inside the visible part.",
            [],
            Name: "drawings-cropbox",
            Draws: FiveKinds()),
        new("drawings-foreign.pdf", null, "Drawings of another program (ink with and without appearance, squares, circle, line with arrowhead, ink with a reply). Pagewing adds its own and moves its freehand stroke; the foreign ones stay as they are. Page 2 has /Rotate 90 and a CropBox.",
            [],
            Name: "drawings-drawings-foreign",
            Draws:
            [
                new(0, AnnotationKind.Ink, 0.05, 0.49, 0.20, 0.60, DrawColor.Red, StrokeWidth.Medium, null, 0, 60),
                new(0, AnnotationKind.Rectangle, 0.22, 0.49, 0.37, 0.60, DrawColor.Blue, StrokeWidth.Medium),
                new(0, AnnotationKind.Ellipse, 0.39, 0.49, 0.54, 0.60, DrawColor.Green, StrokeWidth.Medium),
                new(0, AnnotationKind.Line, 0.56, 0.49, 0.71, 0.60, DrawColor.Black, StrokeWidth.Medium),
                new(0, AnnotationKind.Arrow, 0.73, 0.49, 0.95, 0.60, DrawColor.Orange, StrokeWidth.Medium),
                new(1, AnnotationKind.Ink, 0.10, 0.60, 0.80, 0.75, DrawColor.Red, StrokeWidth.Medium),
                new(1, AnnotationKind.Arrow, 0.15, 0.82, 0.85, 0.92, DrawColor.Blue, StrokeWidth.Medium),
            ]),
        new("text-basic.pdf", null, "Five drawings, every one moved after it was made (1st, 3rd and 5th by -25/-15 pt, 2nd and 4th by +45/+30 pt, right/down positive; a move stays on the page). Each must be there exactly once, at the new place.",
            [],
            Name: "drawings-moved",
            Draws: [.. FiveKinds().Select((d, i) => d with { MoveX = i % 2 == 0 ? -25 : 45, MoveY = i % 2 == 0 ? -15 : 30 })]),
        new("text-basic.pdf", null, "Five drawings, every one recoloured after it was made. Each must show the new colour only.",
            [],
            Name: "drawings-recolored",
            Draws: [.. FiveKinds().Select((d, i) => d with { RecolorTo = DrawPalette.All[(i + 2) % DrawPalette.All.Count] })]),
    ];

    /// <summary>The samples: source, password and the changes, in this order.</summary>
    private static IReadOnlyList<Sample> Samples { get; } =
    [
        new("text-basic.pdf", null, "Two plain pages with one line of text each.",
        [
            new Mark(0, "Hello", AnnotationKind.Highlight, MarkupColor.Yellow, "Interop: yellow highlight on 'Hello'"),
            new Mark(0, "Pagewing", AnnotationKind.Underline, MarkupColor.Blue, null),
            new Mark(1, "Größe", AnnotationKind.StrikeOut, MarkupColor.Pink, null),
        ]),
        new("text-rich.pdf", null, "Two pages, headings, a link and a selection over two lines.",
        [
            new Mark(0, "Handbuch", AnnotationKind.Highlight, MarkupColor.Green, null),
            new Mark(0, "schneller PDF-Viewer für Win-dows und Linux.", AnnotationKind.Highlight, MarkupColor.Orange, "Interop: orange highlight over two lines",
                Shown: "two lines, from \"schneller\" to \"Win-\" (line 1) and on from \"dows\" to \"Linux.\" (line 2), one markup with two quadrilaterals",
                Lines: 2),
            new Mark(0, "https://example.org/pagewing", AnnotationKind.Underline, MarkupColor.Blue, null),
            new Mark(0, "kursiv", AnnotationKind.StrikeOut, MarkupColor.Pink, null),
            new Mark(1, "Seite zwei", AnnotationKind.Highlight, MarkupColor.Yellow, "Interop: highlight on page 2"),
        ]),
        new("rotated-page.pdf", null, "The page has /Rotate 90.",
        [
            new Mark(0, "Rotated", AnnotationKind.Highlight, MarkupColor.Yellow, "Interop: highlight on a rotated page"),
        ]),
        new("cropbox.pdf", null, "The CropBox is smaller than the MediaBox and offset.",
        [
            new Mark(0, "CropBox", AnnotationKind.Highlight, MarkupColor.Orange, "Interop: highlight inside a CropBox"),
        ]),
        new("annotations.pdf", null, "Already has annotations of another program; Pagewing recolours the foreign highlight.",
        [
            new Mark(0, "Link area", AnnotationKind.Underline, MarkupColor.Green, null),
        ],
        Recolor: new Recolor(0, "Highlight", MarkupColor.Blue, "Interop: foreign highlight recoloured to blue")),
        new("annotations-foreign.pdf", null, "Annotations of another program (reply, popup, ink, free text, note, link); Pagewing deletes the bare green highlight.",
        [
            new Mark(0, "Ink is drawn below.", AnnotationKind.Highlight, MarkupColor.Green, "Interop: new highlight next to foreign annotations"),
            new Mark(1, "underlined", AnnotationKind.Highlight, MarkupColor.Yellow, null),
        ],
        Delete: new Delete(0, "No name, no appearance")),
        new("signed-docmdp-3.pdf", null, "Signature dictionary with DocMDP level 3 (forms and annotations allowed); a structure dummy without a valid signature.",
        [
            new Mark(0, "Signed document", AnnotationKind.Highlight, MarkupColor.Yellow, "Interop: annotated after signing (DocMDP 3)"),
        ]),
        new("signed-docmdp-1.pdf", null, "Signature dictionary with DocMDP level 1 (no changes allowed); a structure dummy without a valid signature. The app warns before it saves such a file; this sample is saved anyway to see what viewers report.",
        [
            new Mark(0, "Signed document", AnnotationKind.Highlight, MarkupColor.Yellow, "Interop: annotated although DocMDP 1 forbids changes"),
        ]),
        new("encrypted_hello_world_r6.pdf", UserPassword, "Encrypted (AES-256, revision 6), user password 'hôtel'. The added objects are encrypted as well.",
        [
            new Mark(0, "Hello", AnnotationKind.Highlight, MarkupColor.Yellow, "Interop: highlight in an encrypted file"),
        ]),
        new("linearized.pdf", null, "Linearized (fast web view), two pages; the saved file is no longer linearized (incremental update).",
        [
            new Mark(0, "page one", AnnotationKind.Highlight, MarkupColor.Yellow, null),
            new Mark(1, "page two", AnnotationKind.Underline, MarkupColor.Blue, "Interop: underline on page 2"),
        ]),
        .. DrawingSamples,
    ];

    [Fact]
    public async Task WritesAnnotatedSamplesForOtherViewers()
    {
        var output = Environment.GetEnvironmentVariable(OutputVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(output), $"Interop samples only: set {OutputVariable}=<folder>.");
        output = Directory.CreateDirectory(output).FullName;

        var engine = new PdfEngine();
        var readme = new StringBuilder();
        WriteReadmeHeader(readme);
        using var work = new TempFolder();
        foreach (var sample in Samples)
        {
            var report = await WriteSampleAsync(engine, sample, work.Path, output);
            readme.Append(report);
        }

        await File.WriteAllTextAsync(Path.Combine(output, "README-interop.txt"), readme.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), Ct);

        // The sources, their annotated copies and the readme are there; no temporary file of a save is left behind.
        foreach (var name in Samples.SelectMany(s => new[] { s.File, AnnotatedName(s) }).Append("README-interop.txt"))
        {
            Assert.True(File.Exists(Path.Combine(output, name)), $"{name} is missing in {output}.");
        }

        Assert.Empty(Directory.GetFiles(output, "*.tmp"));
    }

    private static string AnnotatedName(Sample sample) => (sample.Name ?? Path.GetFileNameWithoutExtension(sample.File)) + "__annotated.pdf";

    /// <summary>One drawing of each kind in a 2 x 3 arrangement, for the pages that differ only in rotation or CropBox.</summary>
    private static Draw[] FiveKinds() =>
    [
        new(0, AnnotationKind.Ink, 0.10, 0.20, 0.45, 0.32, DrawColor.Red, StrokeWidth.Medium),
        new(0, AnnotationKind.Rectangle, 0.55, 0.20, 0.90, 0.32, DrawColor.Blue, StrokeWidth.Thick),
        new(0, AnnotationKind.Ellipse, 0.10, 0.40, 0.45, 0.55, DrawColor.Green, StrokeWidth.Medium, "Interop: ellipse"),
        new(0, AnnotationKind.Line, 0.55, 0.42, 0.90, 0.55, DrawColor.Black, StrokeWidth.Thin),
        new(0, AnnotationKind.Arrow, 0.10, 0.65, 0.50, 0.80, DrawColor.Orange, StrokeWidth.Thick),
    ];

    private static async Task<string> WriteSampleAsync(PdfEngine engine, Sample sample, string workFolder, string output)
    {
        var source = TestData.Pdf(sample.File);
        var original = await File.ReadAllBytesAsync(source, Ct);
        var working = Path.Combine(workFolder, sample.Name is null ? sample.File : sample.Name + ".pdf");
        File.Copy(source, working, overwrite: true);

        var expected = new List<Expected>();
        var drawn = new List<ExpectedDraw>();
        IReadOnlyDictionary<int, List<ForeignView>>? foreignBefore = null;
        string? deletedComment = null;
        string? recolored = null;
        using (var session = new ViewerSession(engine))
        {
            await session.OpenAsync(working, sample.Password);
            Assert.Equal(ViewerState.Ready, session.State);
            session.Document!.Clock = new FixedClock(Now);
            var editor = new AnnotationEditor(session);
            if (sample.Draws is not null)
            {
                foreignBefore = await ForeignAsync(session.Document!, new HashSet<string>());
            }

            foreach (var mark in sample.Marks)
            {
                var rects = await RectsOfAsync(session.Document!, mark);
                var created = await editor.ApplyMarkupAsync(mark.Page, mark.Kind, rects, AnnotationPalette.ColorOf(mark.Color), mark.Comment, Author);
                Assert.NotNull(created);
                Assert.Equal(mark.Lines, created.QuadRects.Count);
                expected.Add(new Expected(created.Name!, mark.Page, mark.Kind, AnnotationPalette.ColorOf(mark.Color), mark.Comment, created.QuadRects));
            }

            if (sample.Recolor is { } recolor)
            {
                var foreign = (await session.Document!.GetAnnotationsAsync(recolor.Page, Ct)).First(a => a.Subtype == recolor.Subtype && a.Editable);
                var changed = await editor.UpdateAsync(new AnnotationLocator(recolor.Page, foreign.Index, foreign.Name), new AnnotationEdit(recolor.Comment, AnnotationPalette.ColorOf(recolor.Color)));
                expected.Add(new Expected(changed.Name!, recolor.Page, changed.Kind, AnnotationPalette.ColorOf(recolor.Color), recolor.Comment, changed.QuadRects));
                recolored = changed.Name;
            }

            if (sample.Delete is { } delete)
            {
                var foreign = (await session.Document!.GetAnnotationsAsync(delete.Page, Ct)).Single(a => a.Contents == delete.Comment);
                await editor.DeleteAsync(new AnnotationLocator(delete.Page, foreign.Index, foreign.Name));
                deletedComment = delete.Comment;
            }

            foreach (var draw in sample.Draws ?? [])
            {
                drawn.Add(await DrawAsync(session.Document!, editor, draw));
            }

            Assert.True(editor.IsDirty);
            var saved = await editor.SaveAsync(engine, working, overwriteChangedOriginal: false, progress: null, Ct);
            Assert.True(saved.Adopted);
            Assert.False(editor.IsDirty);
        }

        var bytes = await File.ReadAllBytesAsync(working, Ct);

        // The original bytes are untouched (signed files keep the signed byte range).
        Assert.True(bytes.Length > original.Length, $"{sample.File}: nothing was appended.");
        Assert.True(bytes.AsSpan(0, original.Length).SequenceEqual(original), $"{sample.File}: the original bytes were changed.");

        await CheckRoundTripAsync(engine, sample, working, expected, deletedComment);
        CheckAppearanceObjects(sample, original, bytes, expected);
        var orphans = 0;
        if (sample.Draws is not null)
        {
            await CheckDrawingsAsync(engine, sample, working, drawn, foreignBefore!);
            orphans = CheckDrawingObjects(sample, original, bytes, drawn);
        }

        File.Copy(source, Path.Combine(output, sample.File), overwrite: true);
        File.Copy(working, Path.Combine(output, AnnotatedName(sample)), overwrite: true);
        return Describe(sample, expected, drawn, orphans, recolored is not null, original.Length, bytes.Length);
    }

    /// <summary>The selection rectangles of the text of a mark, as the view selects it (character rectangles per line).</summary>
    private static async Task<IReadOnlyList<PageRect>> RectsOfAsync(PdfDocument document, Mark mark)
    {
        var text = await document.GetPageTextAsync(mark.Page, Ct);
        var all = text.GetText(0, text.Chars.Count);
        var start = IndexOfIgnoringBreaks(all, mark.Text, out var length);
        Assert.True(start >= 0, $"'{mark.Text}' is not on page {mark.Page}: '{all}'");
        var rects = text.RectsOf(start, start + length);
        Assert.NotEmpty(rects);
        return rects;
    }

    /// <summary>Finds <paramref name="needle"/> in <paramref name="haystack"/>, where a line break may stand between two characters of it.</summary>
    private static int IndexOfIgnoringBreaks(string haystack, string needle, out int length)
    {
        var direct = haystack.IndexOf(needle, StringComparison.Ordinal);
        if (direct >= 0)
        {
            length = needle.Length;
            return direct;
        }

        for (var start = 0; start < haystack.Length; start++)
        {
            int h = start, n = 0;
            while (h < haystack.Length && n < needle.Length)
            {
                if (haystack[h] == needle[n])
                {
                    h++;
                    n++;
                }
                else if (n > 0 && haystack[h] is '\r' or '\n')
                {
                    h++;
                }
                else
                {
                    break;
                }
            }

            if (n == needle.Length)
            {
                length = h - start;
                return start;
            }
        }

        length = 0;
        return -1;
    }

    /// <summary>The saved file opens with PDFium, renders, and lists the markups with the expected kinds, colours, lines and comments.</summary>
    private static async Task CheckRoundTripAsync(PdfEngine engine, Sample sample, string path, List<Expected> expected, string? deletedComment)
    {
        if (sample.Password is not null)
        {
            var refused = await Assert.ThrowsAsync<PdfOpenException>(() => engine.OpenAsync(path, cancellationToken: Ct));
            Assert.Equal(PdfOpenError.PasswordRequired, refused.Error);
        }

        await using var reopened = await engine.OpenAsync(path, sample.Password, Ct);
        foreach (var page in expected.Select(e => e.Page).Distinct())
        {
            _ = await reopened.RenderPageAsync(page, 1, cancellationToken: Ct);
        }

        foreach (var want in expected)
        {
            var annotations = await reopened.GetAnnotationsAsync(want.Page, Ct);
            var got = annotations.SingleOrDefault(a => a.Name == want.Name);
            Assert.True(got is not null, $"{sample.File}: the markup {want.Name} is missing on page {want.Page}.");
            Assert.Equal(want.Kind, got.Kind);
            Assert.Equal(want.Color, got.Color);
            Assert.Equal(want.Comment, got.Contents);
            Assert.True(got.Editable);
            Assert.Equal(want.Lines.Count, got.QuadRects.Count);
            for (var i = 0; i < want.Lines.Count; i++)
            {
                AssertRectNear(want.Lines[i], got.QuadRects[i], 0.1);
            }
        }

        if (deletedComment is not null)
        {
            for (var page = 0; page < reopened.PageCount; page++)
            {
                Assert.DoesNotContain(await reopened.GetAnnotationsAsync(page, Ct), a => a.Contents == deletedComment);
            }
        }
    }

    /// <summary>
    /// Every created or changed markup is an indirect object of the appended revision whose <c>/AP /N</c> names a form
    /// XObject that is an object of its own in that revision. Names are strings, which an encrypted file encrypts, so there
    /// the markups are counted by subtype instead of found by <c>/NM</c>.
    /// </summary>
    private static void CheckAppearanceObjects(Sample sample, byte[] original, byte[] saved, List<Expected> expected)
    {
        var appended = Encoding.Latin1.GetString(saved, original.Length, saved.Length - original.Length);
        var objects = ObjectPattern().Matches(appended).ToDictionary(m => m.Groups["number"].Value, m => m.Groups["body"].Value);
        var markups = objects.Values.Where(body => MarkupPattern().IsMatch(body)).ToList();
        Assert.True(markups.Count >= expected.Count, $"{sample.File}: {markups.Count} markup objects in the appended revision, expected at least {expected.Count}.");
        if (sample.Password is null)
        {
            markups = [.. expected.Select(want => markups.Single(body => body.Contains("/NM(" + want.Name + ")", StringComparison.Ordinal)))];
        }

        foreach (var body in markups)
        {
            var reference = AppearancePattern().Match(body);
            Assert.True(reference.Success, $"{sample.File}: a markup has no /AP << /N n 0 R >>: {body}");
            Assert.True(objects.TryGetValue(reference.Groups["n"].Value, out var appearance), $"{sample.File}: the appearance object {reference.Groups["n"].Value} is not in the appended revision.");
            var stream = appearance.IndexOf("stream", StringComparison.Ordinal);
            Assert.Matches(FormPattern(), stream < 0 ? appearance : appearance[..stream]);
        }
    }

    /// <summary>Makes one drawing of a sample through the editor, then moves and recolours it when the sample says so.</summary>
    private static async Task<ExpectedDraw> DrawAsync(PdfDocument document, AnnotationEditor editor, Draw draw)
    {
        var size = await document.GetPageSizeAsync(draw.Page, Ct);
        var page = new PageRect(0, 0, size.Width, size.Height);
        var geometry = GeometryOf(draw, size);
        var color = DrawPalette.ColorOf(draw.Color);
        var created = await editor.ApplyDrawingAsync(draw.Page, geometry, color, draw.Comment, Author);
        Assert.NotNull(created);
        Assert.True(created.GeometryEditable);
        Assert.Equal(draw.Kind, created.Kind);
        var current = created;
        var original = created.Drawing!;
        DrawColor? recoloredFrom = null;
        if (draw.MoveX != 0 || draw.MoveY != 0)
        {
            var moved = DrawingTransform.Move(original, draw.MoveX, draw.MoveY, page);
            current = await editor.ReplaceGeometryAsync(new AnnotationLocator(draw.Page, created.Index, created.Name), moved);
            Assert.Equal(created.Name, current.Name);
            Assert.NotEqual(original.Box, current.Drawing!.Box);
        }

        if (draw.RecolorTo is { } recolor)
        {
            recoloredFrom = draw.Color;
            color = DrawPalette.ColorOf(recolor);
            current = await editor.UpdateAsync(new AnnotationLocator(draw.Page, current.Index, current.Name), new AnnotationEdit(Color: color));
            Assert.Equal(color, current.Color);
        }

        var final = current.Drawing!;
        Assert.True(
            final.Box.Left >= -0.001 && final.Box.Top >= -0.001 && final.Box.Right <= size.Width + 0.001 && final.Box.Bottom <= size.Height + 0.001,
            $"{draw.Kind} {final.Box} leaves the page {size}.");
        return new ExpectedDraw(current.Name!, draw.Page, draw.Kind, color, draw.Comment, final, original, recoloredFrom);
    }

    /// <summary>The shape of a draw as the tools make it: a freehand stroke is thinned and smoothed as the view does on release.</summary>
    private static DrawingGeometry GeometryOf(Draw draw, PdfPageSize size)
    {
        DisplayPoint From(double x, double y) => new(x * size.Width, y * size.Height);
        var width = StrokeWidths.PointsOf(draw.Width);
        switch (draw.Kind)
        {
            case AnnotationKind.Ink:
                // A wave of 160 raw points across the box, as a pen at 120 Hz would deliver it.
                var raw = new List<DisplayPoint>();
                for (var i = 0; i <= 160; i++)
                {
                    var t = i / 160.0;
                    raw.Add(From(draw.X0 + ((draw.X1 - draw.X0) * t), ((draw.Y0 + draw.Y1) / 2) + ((draw.Y1 - draw.Y0) / 2 * Math.Sin(t * 3 * Math.PI))));
                }

                return DrawingGeometry.Ink([StrokeSimplifier.Smooth(StrokeSimplifier.Simplify(raw, 0.5))], width);
            case AnnotationKind.Line:
                return DrawingGeometry.Line(From(draw.X0, draw.Y0), From(draw.X1, draw.Y1), width);
            case AnnotationKind.Arrow:
                return DrawingGeometry.Arrow(From(draw.X0, draw.Y0), From(draw.X1, draw.Y1), width);
            default:
                var box = new PageRect(draw.X0 * size.Width, draw.Y0 * size.Height, draw.X1 * size.Width, draw.Y1 * size.Height);
                return draw.Kind == AnnotationKind.Rectangle ? DrawingGeometry.Rectangle(box, width) : DrawingGeometry.Ellipse(box, width);
        }
    }

    /// <summary>What the annotations of other programs look like, per page, without those whose names are in <paramref name="except"/>.</summary>
    private static async Task<IReadOnlyDictionary<int, List<ForeignView>>> ForeignAsync(PdfDocument document, HashSet<string> except)
    {
        var result = new Dictionary<int, List<ForeignView>>();
        for (var page = 0; page < document.PageCount; page++)
        {
            var list = new List<ForeignView>();
            foreach (var a in await document.GetAnnotationsAsync(page, Ct))
            {
                if (a.Name is not null && except.Contains(a.Name))
                {
                    continue;
                }

                var key = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{a.Subtype}|{a.Name}|{a.Contents}|{a.Author}|{a.Editable}|{a.GeometryEditable}|{ShapeOf(a.Drawing)}");
                list.Add(new ForeignView(key, a.Color, a.Bounds));
            }

            list.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
            result[page] = list;
        }

        return result;
    }

    private static string ShapeOf(DrawingGeometry? g) => g is null
        ? "-"
        : string.Create(
            CultureInfo.InvariantCulture,
            $"{g.Kind} {string.Join(';', g.Strokes.Select(s => string.Join(',', s.Select(p => $"{p.X:F3} {p.Y:F3}"))))} {g.Box.Left:F3} {g.Box.Top:F3} {g.Box.Right:F3} {g.Box.Bottom:F3}");

    /// <summary>The saved file lists every drawing as made (kind, colour, comment, shape), once, and the annotations of other programs unchanged.</summary>
    private static async Task CheckDrawingsAsync(PdfEngine engine, Sample sample, string path, List<ExpectedDraw> drawn, IReadOnlyDictionary<int, List<ForeignView>> foreignBefore)
    {
        await using var reopened = await engine.OpenAsync(path, sample.Password, Ct);
        foreach (var page in drawn.Select(d => d.Page).Distinct())
        {
            _ = await reopened.RenderPageAsync(page, 1, cancellationToken: Ct);
        }

        foreach (var want in drawn)
        {
            var annotations = await reopened.GetAnnotationsAsync(want.Page, Ct);
            var one = Assert.Single(annotations, a => a.Name == want.Name);
            Assert.Equal(want.Kind, one.Kind);
            Assert.Equal(want.Color, one.Color);
            Assert.Equal(want.Comment, one.Contents);
            Assert.Equal(Author, one.Author);
            Assert.True(one.Editable);
            Assert.True(one.GeometryEditable);
            AssertSameGeometry(want.Geometry, one.Drawing!, $"{sample.Name}: {want.Kind} on page {want.Page}");
        }

        var names = drawn.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        var foreignAfter = await ForeignAsync(reopened, names);
        foreach (var (page, before) in foreignBefore)
        {
            // Same annotations with the same name, box, comment, author and shape. PDFium writes the objects it has loaded again
            // with its own number format (/C [1 0.5 0] comes back as 0.501961): the colour may differ by one step of 8 bits.
            Assert.Equal(before.Select(f => f.Key), foreignAfter[page].Select(f => f.Key));
            for (var i = 0; i < before.Count; i++)
            {
                var (x, y) = (before[i].Color, foreignAfter[page][i].Color);
                Assert.Equal(x is null, y is null);
                if (x is { } a && y is { } b)
                {
                    Assert.True(Math.Abs(a.R - b.R) <= 1 && Math.Abs(a.G - b.G) <= 1 && Math.Abs(a.B - b.B) <= 1, $"{before[i].Key}: {a} vs {b}");
                }

                // PDFium builds the appearance of an annotation that has none when it loads the page, and then also adjusts
                // /Rect to the stroke it draws (a few points at most); a foreign annotation is not byte-identical after a save.
                AssertRectNear(before[i].Bounds, foreignAfter[page][i].Bounds, 4);
            }
        }
    }

    private static void AssertSameGeometry(DrawingGeometry want, DrawingGeometry got, string what)
    {
        Assert.Equal(want.Kind, got.Kind);
        Assert.True(Math.Abs(want.Width - got.Width) < 0.01, $"{what}: width {want.Width} vs {got.Width}");
        AssertRectNear(want.Box, got.Box, 0.01);
        Assert.Equal(want.Strokes.Count, got.Strokes.Count);
        for (var s = 0; s < want.Strokes.Count; s++)
        {
            Assert.Equal(want.Strokes[s].Count, got.Strokes[s].Count);
            for (var i = 0; i < want.Strokes[s].Count; i++)
            {
                Assert.True(
                    Math.Abs(want.Strokes[s][i].X - got.Strokes[s][i].X) < 0.01 && Math.Abs(want.Strokes[s][i].Y - got.Strokes[s][i].Y) < 0.01,
                    $"{what}: point {i} of stroke {s}");
            }
        }
    }

    /// <summary>
    /// Every drawing is an indirect object of the appended revision whose <c>/AP /N</c> is a form XObject of its own in that
    /// revision; a line and an arrow carry their <c>/Subj</c>. Names are strings (the samples of drawings are not encrypted),
    /// so the objects are found by <c>/NM</c>.
    /// </summary>
    private static int CheckDrawingObjects(Sample sample, byte[] original, byte[] saved, List<ExpectedDraw> drawn)
    {
        var appended = Encoding.Latin1.GetString(saved, original.Length, saved.Length - original.Length);
        var objects = ObjectPattern().Matches(appended).ToDictionary(m => m.Groups["number"].Value, m => m.Groups["body"].Value);
        var listed = AnnotsReferences(objects);
        var orphans = 0;
        foreach (var want in drawn)
        {
            // A drawing that was replaced (moved, recoloured) before the first save leaves its first version in the revision as an
            // object nothing refers to; the live one is the object a page lists in /Annots, exactly one.
            var named = objects.Where(o => o.Value.Contains("/NM(" + want.Name + ")", StringComparison.Ordinal)).ToList();
            var body = Assert.Single(named, o => listed.Contains(o.Key)).Value;
            orphans += named.Count - 1;
            var subtype = want.Kind switch
            {
                AnnotationKind.Rectangle => "Square",
                AnnotationKind.Ellipse => "Circle",
                _ => "Ink",
            };
            Assert.Matches(@"/Subtype\s*/" + subtype + @"\b", body);
            if (want.Kind is AnnotationKind.Line or AnnotationKind.Arrow)
            {
                Assert.Contains("/Subj(" + (want.Kind == AnnotationKind.Arrow ? "Arrow" : "Line") + ")", body, StringComparison.Ordinal);
            }

            var reference = AppearancePattern().Match(body);
            Assert.True(reference.Success, $"{sample.Name}: a drawing has no /AP << /N n 0 R >>: {body}");
            Assert.True(objects.TryGetValue(reference.Groups["n"].Value, out var appearance), $"{sample.Name}: the appearance object {reference.Groups["n"].Value} is not in the appended revision.");
            var stream = appearance.IndexOf("stream", StringComparison.Ordinal);
            Assert.Matches(FormPattern(), stream < 0 ? appearance : appearance[..stream]);
        }

        return orphans;
    }

    /// <summary>The numbers of the objects that a page of the appended revision lists in its <c>/Annots</c> (a direct or an indirect array).</summary>
    private static HashSet<string> AnnotsReferences(Dictionary<string, string> objects)
    {
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var body in objects.Values.Where(b => b.Contains("/Annots", StringComparison.Ordinal)))
        {
            var direct = AnnotsPattern().Match(body);
            var list = direct.Success ? direct.Groups["list"].Value : null;
            if (list is null && AnnotsIndirectPattern().Match(body) is { Success: true } indirect && objects.TryGetValue(indirect.Groups["n"].Value, out var array))
            {
                list = array;
            }

            foreach (Match reference in ReferencePattern().Matches(list ?? string.Empty))
            {
                listed.Add(reference.Groups["n"].Value);
            }
        }

        return listed;
    }

    private static void WriteReadmeHeader(StringBuilder readme)
    {
        readme.AppendLine("Pagewing - annotation samples for other viewers");
        readme.AppendLine("================================================");
        readme.AppendLine();
        readme.AppendLine("Written by tests/Pagewing.Core.Tests/Annotations/InteropSampleTests.cs (PAGEWING_INTEROP_OUT).");
        readme.AppendLine("Every <name>.pdf is the unchanged source from testdata/pdfs, every <name>__annotated.pdf the same file");
        readme.AppendLine("annotated by Pagewing's own pipeline (AnnotationEditor, then Save through DocumentSaver: incremental update,");
        readme.AppendLine("checked before it replaced the file). Open both side by side; the checklist is in packaging/README.md,");
        readme.AppendLine("section \"Annotationen im Fremdviewer prüfen\".");
        readme.AppendLine();
        readme.AppendLine(CultureInfo.InvariantCulture, $"Author of every new markup: {Author}. Date of every change: {Now:yyyy-MM-dd HH:mm} UTC.");
        readme.AppendLine("The /NM names are random and differ from run to run; everything else is fixed.");
        readme.AppendLine();
        readme.AppendLine("Colours (palette of Pagewing, RGB):");
        foreach (var color in AnnotationPalette.All)
        {
            var value = AnnotationPalette.ColorOf(color);
            readme.AppendLine(CultureInfo.InvariantCulture, $"  {color,-7} #{value.R:X2}{value.G:X2}{value.B:X2}");
        }

        readme.AppendLine();
        readme.AppendLine("For every markup a viewer must show:");
        readme.AppendLine("  - highlight: a coloured area behind the words, the words stay readable (multiplied, not covered);");
        readme.AppendLine("  - underline: a coloured line just below the words; strike-out: a coloured line through their middle;");
        readme.AppendLine("  - exactly over the words named below, also on the rotated page and inside the CropBox;");
        readme.AppendLine("  - a comment, where one is named: in the comment list (Acrobat: Comments pane) and on hover or click;");
        readme.AppendLine("  - the same markups after printing or print preview (the print flag is set).");
        readme.AppendLine("Pagewing never claims that a signature is valid; for the signed files only report what the viewer says.");
        readme.AppendLine();
        readme.AppendLine("Drawings (the drawings-*__annotated.pdf files, milestone 10): for every drawing a viewer must show:");
        readme.AppendLine("  - the stroke in the colour and width named below (palette: red #DC1E1E, black #000000, blue #1E50DC, green #148C3C, orange #F08200);");
        readme.AppendLine("  - freehand: the smooth wave, no corners and no gaps; rectangle and ellipse: the outline inside the box, not outside it,");
        readme.AppendLine("    not filled; line: one straight stroke; arrow: a shaft with an open head whose tip is the end of the box and is not cut off;");
        readme.AppendLine("  - on the rotated page and inside the CropBox at the place the box names (measured on the page as it looks on screen);");
        readme.AppendLine("  - drawings of other programs (drawings-foreign.pdf, annotations.pdf) as in the source file (see the note per file);");
        readme.AppendLine("  - MOVED drawings exactly once, at the new place, nothing at the old one (no ghost);");
        readme.AppendLine("  - RECOLOURED drawings in the new colour only (no outline of the old colour behind it);");
        readme.AppendLine("  - Acrobat lists the line and the arrow as freehand (ink) in its comment list: expected. Note whether it still");
        readme.AppendLine("    shows the line/arrow subject after Acrobat saves the file itself (Pagewing writes /Subj (Line) and /Subj (Arrow)).");
        readme.AppendLine();
    }

    private static string Describe(Sample sample, List<Expected> expected, List<ExpectedDraw> drawn, int orphans, bool recolored, long originalLength, long savedLength)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"{sample.File} -> {AnnotatedName(sample)}");
        text.AppendLine(new string('-', sample.File.Length + AnnotatedName(sample).Length + 4));
        text.AppendLine(CultureInfo.InvariantCulture, $"  {sample.About}");
        if (sample.Password is not null)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  Password: {sample.Password} (both files ask for it; the annotated one must show the markup after it).");
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"  Original {originalLength} bytes, annotated {savedLength} bytes; the first {originalLength} bytes are identical.");
        foreach (var mark in sample.Marks)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  - page {mark.Page + 1}: {KindName(mark.Kind)} {mark.Color} on {mark.Shown ?? $"\"{mark.Text}\""}{CommentOf(mark.Comment)}");
        }

        if (sample.Recolor is { } recolor && recolored)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  - page {recolor.Page + 1}: the existing yellow highlight of the other program is now {recolor.Color}{CommentOf(recolor.Comment)} (Pagewing made a new appearance)");
        }

        if (sample.Delete is { } delete)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  - page {delete.Page + 1}: the green highlight of the other program with the comment \"{delete.Comment}\" is deleted (over \"Second mark without a name.\"); the other foreign annotations stay");
        }

        if (sample.File.StartsWith("signed-docmdp-3", StringComparison.Ordinal))
        {
            text.AppendLine("  Signature: DocMDP 3 allows annotations. With a real signature a viewer reports the signature as valid with");
            text.AppendLine("  later changes that are permitted (Acrobat: \"changes permitted by the certifier\"). This dummy has no valid");
            text.AppendLine("  signature, so expect \"invalid\"/\"not verifiable\" for that reason, in the source as well as in the annotated copy.");
        }
        else if (sample.File.StartsWith("signed-docmdp-1", StringComparison.Ordinal))
        {
            text.AppendLine("  Signature: DocMDP 1 forbids every change. With a real signature a viewer reports the annotated copy as changed");
            text.AppendLine("  after certification (invalid). This dummy is not verifiable in either file; compare source and copy.");
        }

        foreach (var draw in drawn)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  - page {draw.Page + 1}: {DrawingDescription(draw)}");
        }

        if (drawn.Count > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {drawn.Count} drawing(s) created by Pagewing.");
            if (sample.File == "drawings-foreign.pdf")
            {
                text.AppendLine("  Annotations of other programs: same kind, name, comment, author, colour (+-1/255) and shape, and the box moves by at most");
                text.AppendLine("  4 points. Those WITHOUT an appearance of their own get one from PDFium when the file is saved (/PDFIUM_HasGeneratedAP):");
                text.AppendLine("  drawings-foreign.pdf ink-0002, square-0003, square-0004 (cloud border /BE becomes a plain frame?), circle-0006. Note per viewer how they look");
                text.AppendLine("  next to the source file; the line with an arrowhead (line-0007) has none and PDFium does not make one.");
            }

            if (orphans > 0)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  The appended revision also holds {orphans} unreferenced first version(s) of replaced drawings (no page lists them; a viewer must not show them).");
            }
        }
        else
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {expected.Count} markup(s) created or changed by Pagewing.");
        }
        text.AppendLine();
        return text.ToString();
    }

    private static string DrawingDescription(ExpectedDraw draw)
    {
        var colour = draw.RecoloredFrom is { } from
            ? $"{ColorName(draw.Color)} (made {from.ToString().ToLowerInvariant()}, then recoloured; the old colour must be gone)"
            : ColorName(draw.Color);
        var kind = draw.Kind switch
        {
            AnnotationKind.Ink => "freehand stroke",
            AnnotationKind.Rectangle => "rectangle",
            AnnotationKind.Ellipse => "ellipse",
            AnnotationKind.Line => "line",
            _ => "arrow",
        };
        var box = draw.Geometry.Box;
        var text = string.Create(CultureInfo.InvariantCulture, $"{kind}, {colour}, {draw.Geometry.Width:0.#} pt, box {box.Left:0.#},{box.Top:0.#} to {box.Right:0.#},{box.Bottom:0.#} pt from the top left{CommentOf(draw.Comment)}");
        var from0 = draw.OriginalGeometry.Box;
        if (from0 != box)
        {
            text += string.Create(CultureInfo.InvariantCulture, $"; MOVED from {from0.Left:0.#},{from0.Top:0.#} to {from0.Right:0.#},{from0.Bottom:0.#} (the old place must be empty)");
        }

        return text;
    }

    private static string ColorName(AnnotationColor color) =>
        DrawPalette.All.Where(c => DrawPalette.ColorOf(c) == color).Select(c => c.ToString().ToLowerInvariant()).FirstOrDefault() ?? color.ToString();

    private static string KindName(AnnotationKind kind) => kind switch
    {
        AnnotationKind.Highlight => "highlight",
        AnnotationKind.Underline => "underline",
        AnnotationKind.StrikeOut => "strike-out",
        _ => kind.ToString(),
    };

    private static string CommentOf(string? comment) => comment is null ? string.Empty : $", comment \"{comment}\"";

    [GeneratedRegex(@"(?<!\d)(?<number>\d+)\s+0\s+obj\b(?<body>.*?)\bendobj", RegexOptions.Singleline)]
    private static partial Regex ObjectPattern();

    [GeneratedRegex(@"/Type\s*/Annot\b.*/Subtype\s*/(Highlight|Underline|StrikeOut)\b|/Subtype\s*/(Highlight|Underline|StrikeOut)\b.*/Type\s*/Annot\b", RegexOptions.Singleline)]
    private static partial Regex MarkupPattern();

    [GeneratedRegex(@"/Annots\s*\[(?<list>[^\]]*)\]")]
    private static partial Regex AnnotsPattern();

    [GeneratedRegex(@"/Annots\s+(?<n>\d+)\s+0\s+R")]
    private static partial Regex AnnotsIndirectPattern();

    [GeneratedRegex(@"(?<n>\d+)\s+0\s+R")]
    private static partial Regex ReferencePattern();

    [GeneratedRegex(@"/AP\s*<<\s*/N\s+(?<n>\d+)\s+0\s+R\s*>>")]
    private static partial Regex AppearancePattern();

    [GeneratedRegex(@"/Subtype\s*/Form")]
    private static partial Regex FormPattern();

    /// <summary>One sample: the source in <c>testdata/pdfs</c>, its password, and the changes.</summary>
    private sealed record Sample(string File, string? Password, string About, IReadOnlyList<Mark> Marks, Recolor? Recolor = null, Delete? Delete = null, IReadOnlyList<Draw>? Draws = null, string? Name = null);

    /// <summary>
    /// A new drawing of <paramref name="Kind"/> in the box (X0, Y0)-(X1, Y1), given as fractions of the displayed page. For a
    /// line and an arrow the tail is (X0, Y0) and the tip (X1, Y1). It is then moved by (MoveX, MoveY) points and/or recoloured.
    /// </summary>
    private sealed record Draw(int Page, AnnotationKind Kind, double X0, double Y0, double X1, double Y1, DrawColor Color, StrokeWidth Width, string? Comment = null, double MoveX = 0, double MoveY = 0, DrawColor? RecolorTo = null);

    /// <summary>What a drawing must be after saving and reading again.</summary>
    private sealed record ForeignView(string Key, AnnotationColor? Color, PageRect Bounds);

    private sealed record ExpectedDraw(string Name, int Page, AnnotationKind Kind, AnnotationColor Color, string? Comment, DrawingGeometry Geometry, DrawingGeometry OriginalGeometry, DrawColor? RecoloredFrom);

    /// <summary>A new markup over <paramref name="Text"/> (a line break of the page may stand inside it).</summary>
    private sealed record Mark(int Page, string Text, AnnotationKind Kind, MarkupColor Color, string? Comment, string? Shown = null, int Lines = 1);

    /// <summary>A new colour and comment for the first editable annotation of <paramref name="Subtype"/> on the page.</summary>
    private sealed record Recolor(int Page, string Subtype, MarkupColor Color, string Comment);

    /// <summary>Deletes the annotation of another program whose comment is <paramref name="Comment"/>.</summary>
    private sealed record Delete(int Page, string Comment);

    private sealed record Expected(string Name, int Page, AnnotationKind Kind, AnnotationColor Color, string? Comment, IReadOnlyList<PageRect> Lines);
}

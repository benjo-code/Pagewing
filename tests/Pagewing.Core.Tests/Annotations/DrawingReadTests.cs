using System.Globalization;
using System.Text;
using Pagewing.Core.Annotations;
using Pagewing.Pdfium;
using Pagewing.Pdfium.Native;
using Pagewing.TestSupport;
using Xunit;
using static Pagewing.Core.Tests.Annotations.AnnotationTestFiles;

namespace Pagewing.Core.Tests.Annotations;

/// <summary>Reading, creating and locking drawings (ink, square, circle) in <see cref="PdfiumDrawings"/>.</summary>
public sealed class DrawingReadTests
{
    private const string Catalog = "<< /Type /Catalog /Pages 2 0 R >>";

    private const string Pages = "<< /Type /Pages /Count 1 /Kids [3 0 R] >>";

    private static readonly double ArrowMiter = 1 / (2 * Math.Sin(25 * Math.PI / 180));

    [Fact]
    public async Task ReadsInkStrokesInPageCoordinates()
    {
        var drawing = await ReadAsync(TestData.Pdf(DrawingsForeign), 0, 0);

        Assert.NotNull(drawing);
        Assert.Equal(PdfiumDrawingKind.Ink, drawing.Kind);
        Assert.Equal(2, drawing.Strokes.Count);

        // The page is 400 x 300 points without rotation: y = 240 in PDF space is 60 points from the top.
        AssertPoints([(30, 60), (60, 20), (90, 60)], drawing.Strokes[0], 1e-4);
        AssertPoints([(110, 60), (150, 20), (190, 60)], drawing.Strokes[1], 1e-4);
        AssertBox((30, 20, 190, 60), drawing.Box, 1e-4);

        // No /Border: the width is the one its own appearance strokes with ("2 w").
        Assert.Equal(2, drawing.Width);
        Assert.True(drawing.IsComplete);
    }

    [Fact]
    public async Task ReadsSquareAndCircleBox()
    {
        var path = TestData.Pdf(DrawingsForeign);

        var square = await ReadAsync(path, 0, 2);
        var circle = await ReadAsync(path, 0, 5);
        var line = await ReadAsync(path, 0, 6);
        var note = await ReadAsync(path, 0, 8);

        Assert.NotNull(square);
        Assert.Equal(PdfiumDrawingKind.Square, square.Kind);
        Assert.Empty(square.Strokes);
        AssertBox((28, 82, 108, 142), square.Box, 1e-4);
        Assert.NotNull(circle);
        Assert.Equal(PdfiumDrawingKind.Circle, circle.Kind);
        Assert.Empty(circle.Strokes);
        AssertBox((228, 82, 308, 142), circle.Box, 1e-4);

        // A /Line annotation and a note are no drawings of Pagewing.
        Assert.Null(line);
        Assert.Null(note);
    }

    [Fact]
    public async Task RotatedPageMapsPointsToTheDisplayedPage()
    {
        var path = TestData.Pdf(DrawingsForeign);
        var thread = PdfiumThread.Shared;
        var (drawing, size, expected) = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, path, null);
                using var page = document.LoadPage(1);
                var drawing = PdfiumDrawings.ReadGeometry(page, 0);

                // PDFium's own conversion on its 1/16-pt grid is the reference.
                var transform = PdfiumPageText.TransformOf(page);
                var expected = new List<(double X, double Y)>();
                foreach (var (x, y) in new (double, double)[] { (90, 120), (120, 180), (150, 120), (190, 180) })
                {
                    Assert.True(transform.TryToPage(x, y, out var px, out var py));
                    expected.Add((px, py));
                }

                return (drawing, (page.Width, page.Height), expected);
            },
            cancellationToken: TestContext.Current.CancellationToken);

        // MediaBox 400 x 300, CropBox [20 10 380 290], /Rotate 90: the page shows 280 x 360 points.
        Assert.Equal((280.0, 360.0), size);
        Assert.NotNull(drawing);
        var stroke = Assert.Single(drawing.Strokes);
        AssertPoints(expected, stroke, 1.0 / 16);

        // User (90, 120) lies 70 points right of the crop's left edge and 110 points above its bottom.
        AssertPoints([(110, 70), (170, 100), (110, 130), (170, 170)], stroke, 1e-4);
    }

    [Fact]
    public async Task HostileInkListIsCapped()
    {
        using var folder = new TempFolder();
        var huge = new StringBuilder("[");
        for (var i = 0; i < 200_000; i++)
        {
            huge.Append(CultureInfo.InvariantCulture, $"{i % 300} {i % 200} ");
        }

        huge.Append(']');
        var small = new StringBuilder();
        for (var i = 0; i < PdfiumDrawings.MaxInkStrokes + 50; i++)
        {
            small.Append(CultureInfo.InvariantCulture, $"[{10 + (i % 100)} 10 {20 + (i % 100)} 30] ");
        }

        // Derived from the fixture: the first ink gets a stroke of 200,000 points, one with a coordinate far beyond the
        // page and more strokes than MaxInkStrokes; in a second file a stroke with NaN.
        var capped = await DeriveHostileInkAsync(folder, "capped.pdf", $"{huge} [40 40 99999999999.5 50] {small}");
        var nan = await DeriveHostileInkAsync(folder, "nan.pdf", "[10 10 20 30] [7 7 NaN 20 30 30 40 40]");

        var cappedRead = await ReadWithEditableAsync(capped);
        var nanRead = await ReadWithEditableAsync(nan);

        var drawing = cappedRead.Drawing;
        Assert.NotNull(drawing);
        Assert.False(drawing.IsComplete);
        Assert.False(cappedRead.Editable);
        Assert.InRange(drawing.Strokes.Count, 1, PdfiumDrawings.MaxInkStrokes);
        Assert.True(drawing.Strokes.Sum(s => s.Count) <= PdfiumDrawings.MaxInkPoints);

        // The huge stroke does not fit, the one beyond MaxCoordinate is skipped: only the small strokes are left, as many
        // as fit into MaxInkStrokes after the two skipped ones.
        Assert.Equal(PdfiumDrawings.MaxInkStrokes - 2, drawing.Strokes.Count);
        Assert.All(drawing.Strokes, stroke => Assert.Equal(2, stroke.Count));
        Assert.All(drawing.Strokes.SelectMany(s => s), p => Assert.True(p.X <= 400 && p.Y <= 300, $"{p}"));
        AssertPoints([(10, 290), (20, 270)], drawing.Strokes[0], 1e-4);

        // PDFium ends an array at the token NaN, so the stroke arrives as its one point before it; a single point draws
        // nothing and is skipped. Nothing that is not finite reaches Pagewing.
        var stroke = Assert.Single(nanRead.Drawing!.Strokes);
        AssertPoints([(10, 290), (20, 270)], stroke, 1e-4);
        Assert.True(nanRead.Drawing.IsComplete);
    }

    [Fact]
    public async Task ForeignOwnAppearanceIsNotGeometryEditable()
    {
        Assert.False(await IsGeometryEditableAsync(TestData.Pdf(DrawingsForeign), 0, 0));
        Assert.False(await IsGeometryEditableAsync(TestData.Pdf(DrawingsForeign), 0, 4));

        // The same ink without /BS: with an appearance of another program it is locked, without one or with PDFium's own it is not.
        using var folder = new TempFolder();
        const string Ink = "/Type /Annot /Subtype /Ink /Rect [28 238 92 282] /InkList [[30 240 60 280 90 240]] /C [1 0 0] /Border [0 0 2] /F 4";
        var path = WritePdf(folder, "appearances.pdf", [
            Catalog,
            Pages,
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Annots [4 0 R 5 0 R 6 0 R] >>",
            $"<< {Ink} /AP << /N 7 0 R >> >>",
            $"<< {Ink} >>",
            $"<< {Ink} /AP << /N 7 0 R >> /PDFIUM_HasGeneratedAP true >>",
            "<< /Type /XObject /Subtype /Form /BBox [28 238 92 282] /Length 37 >>\nstream\n1 0 0 RG 2 w 30 240 m 60 280 l 90 240 l S\nendstream",
        ]);

        Assert.False(await IsGeometryEditableAsync(path, 0, 0));
        Assert.True(await IsGeometryEditableAsync(path, 0, 1));
        Assert.True(await IsGeometryEditableAsync(path, 0, 2));
    }

    [Fact]
    public async Task DashedBorderAndCloudAreNotGeometryEditable()
    {
        Assert.False(await IsGeometryEditableAsync(TestData.Pdf(DrawingsForeign), 0, 2));
        Assert.False(await IsGeometryEditableAsync(TestData.Pdf(DrawingsForeign), 0, 3));

        // A cloud locks the geometry on its own, without /BS.
        using var folder = new TempFolder();
        const string Square = "/Type /Annot /Subtype /Square /Rect [28 158 108 218] /C [0 0.5 0] /Border [0 0 2] /F 4";
        var path = WritePdf(folder, "cloud.pdf", [
            Catalog,
            Pages,
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Annots [4 0 R 5 0 R] >>",
            $"<< {Square} /BE << /S /C /I 1 >> >>",
            $"<< {Square} >>",
        ]);

        Assert.False(await IsGeometryEditableAsync(path, 0, 0));
        Assert.True(await IsGeometryEditableAsync(path, 0, 1));
    }

    [Fact]
    public async Task ForeignBorderStyleDictionaryLocksTheGeometry()
    {
        // PDFium cannot read inside /BS, so even a solid border style (/S /S) locks the circle; delete and comment stay.
        Assert.False(await IsGeometryEditableAsync(TestData.Pdf(DrawingsForeign), 0, 5));
        Assert.False(await IsGeometryEditableAsync(TestData.Pdf(DrawingsForeign), 0, 1));
    }

    [Fact]
    public async Task AnnotationWithRepliesIsNotEditable()
    {
        Assert.False(await IsGeometryEditableAsync(TestData.Pdf(DrawingsForeign), 0, 7));

        using var folder = new TempFolder();
        const string Ink = "/Type /Annot /Subtype /Ink /Rect [248 58 332 92] /InkList [[250 60 270 90 290 60]] /C [0 0.6 0.6] /Border [0 0 2] /F 4";
        var path = WritePdf(folder, "replies.pdf", [
            Catalog,
            Pages,
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Annots [4 0 R 5 0 R 6 0 R] >>",
            $"<< {Ink} >>",
            $"<< {Ink} >>",
            "<< /Type /Annot /Subtype /Text /Rect [336 58 354 76] /Contents (Nice) /IRT 5 0 R >>",
        ]);

        Assert.True(await IsGeometryEditableAsync(path, 0, 0));
        Assert.False(await IsGeometryEditableAsync(path, 0, 1));
        Assert.False(await IsGeometryEditableAsync(path, 0, 2));
    }

    [Fact]
    public async Task OpacityFillAndBoxDifferenceLockTheGeometry()
    {
        // Without /AP and /BS, so only the key under test can lock: a replaced drawing would be opaque, unfilled and as big as its /Rect.
        using var folder = new TempFolder();
        const string Ink = "/Type /Annot /Subtype /Ink /Rect [28 238 92 282] /InkList [[30 240 60 280 90 240]] /C [1 0 0] /Border [0 0 2] /F 4";
        const string Square = "/Type /Annot /Subtype /Square /Rect [28 158 108 218] /C [0 0.5 0] /Border [0 0 2] /F 4";
        const string Circle = "/Type /Annot /Subtype /Circle /Rect [228 158 308 218] /C [1 0.5 0] /Border [0 0 2] /F 4";
        var path = WritePdf(folder, "opacity-fill-rd.pdf", [
            Catalog,
            Pages,
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Annots [4 0 R 5 0 R 6 0 R 7 0 R 8 0 R 9 0 R 10 0 R 11 0 R] >>",
            $"<< {Ink} /CA 0.5 >>",
            $"<< {Ink} /CA 1 >>",
            $"<< {Ink} /CA /Half >>",
            $"<< {Square} /IC [1 0 0] >>",
            $"<< {Circle} /IC [0 0 1] >>",
            $"<< {Square} /RD [5 5 5 5] >>",
            $"<< {Circle} /RD [5 5 5 5] >>",
            $"<< {Circle} >>",
        ]);

        Assert.False(await IsGeometryEditableAsync(path, 0, 0));
        Assert.True(await IsGeometryEditableAsync(path, 0, 1));
        Assert.False(await IsGeometryEditableAsync(path, 0, 2));
        Assert.False(await IsGeometryEditableAsync(path, 0, 3));
        Assert.False(await IsGeometryEditableAsync(path, 0, 4));
        Assert.False(await IsGeometryEditableAsync(path, 0, 5));
        Assert.False(await IsGeometryEditableAsync(path, 0, 6));
        Assert.True(await IsGeometryEditableAsync(path, 0, 7));
    }

    [Fact]
    public async Task FlagsBesidesPrintLockTheGeometry()
    {
        // A replacement carries Print only, so a drawing with any other flag would turn visible (Hidden, NoView), be edited
        // (Locked, ReadOnly) or lose its behaviour on rotating and zooming (NoRotate, NoZoom).
        int[] locking = [1, 2, 8, 16, 32, 64, 128, 256, 512, 4 | 2, 4 | 32, 4 | 128, 4 | 64, 4 | 16, 4 | 8];
        int[] free = [0, 4];
        using var folder = new TempFolder();
        foreach (var kind in new[] { "Ink", "Square", "Circle" })
        {
            var shape = kind == "Ink"
                ? "/Rect [28 238 92 282] /InkList [[30 240 60 280 90 240]]"
                : "/Rect [28 158 108 218]";
            var objects = new List<string>
            {
                Catalog,
                Pages,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Annots [{string.Join(' ', Enumerable.Range(4, free.Length + locking.Length).Select(n => $"{n} 0 R"))}] >>",
            };
            objects.AddRange(free.Concat(locking).Select(flags => $"<< /Type /Annot /Subtype /{kind} {shape} /C [1 0 0] /Border [0 0 2] /F {flags} >>"));
            var path = WritePdf(folder, $"flags-{kind}.pdf", objects);

            for (var i = 0; i < free.Length; i++)
            {
                Assert.True(await IsGeometryEditableAsync(path, 0, i), $"{kind} with /F {free[i]} must stay editable.");
            }

            for (var i = 0; i < locking.Length; i++)
            {
                Assert.False(await IsGeometryEditableAsync(path, 0, free.Length + i), $"{kind} with /F {locking[i]} must be locked.");
            }
        }
    }

    [Fact]
    public async Task OwnDrawingsStayEditableAfterTheAppearanceAndARecolour()
    {
        using var folder = new TempFolder();
        var copy = Path.Combine(folder.Path, "copy.pdf");
        File.Copy(TestData.Pdf("text-basic.pdf"), copy);
        var specs = new[]
        {
            Spec(PdfiumDrawingKind.Arrow, ArrowGeometry.Strokes(new(120, 120), new(220, 120), 2, head: true), 2),
            new PdfiumDrawing(PdfiumDrawingKind.Square, [], (150, 20, 250, 80), 4),
            new PdfiumDrawing(PdfiumDrawingKind.Circle, [], (150, 100, 230, 160), 1),
        };
        var thread = PdfiumThread.Shared;
        var editable = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, copy, null);
                var indices = new List<int>();
                using (var page = document.LoadPage(0))
                {
                    indices.AddRange(specs.Select(spec => PdfiumDrawings.Create(page, spec, (220, 30, 30), null, null, Guid.NewGuid().ToString("N"), "D:20261025120000Z")));
                }

                var result = new List<bool>();
                Assert.True(PdfiumAnnotations.GenerateAppearance(document, 0, indices[0]));
                using (var page = document.LoadPage(0))
                {
                    result.AddRange(indices.Select(i => PdfiumDrawings.IsGeometryEditable(page, i)));

                    // Pagewing's own drawings carry Print only, as a replacement of a foreign one must be able to rely on.
                    Assert.All(indices, i => Assert.Equal(FpdfConstants.AnnotFlagPrint, FlagsOf(page, i)));
                    foreach (var index in indices)
                    {
                        PdfiumAnnotations.Update(page, index, null, (30, 80, 220), Guid.NewGuid().ToString("N"), "D:20261025130000Z");
                    }
                }

                Assert.True(PdfiumAnnotations.GenerateAppearance(document, 0, indices[0]));
                using (var page = document.LoadPage(0))
                {
                    result.AddRange(indices.Select(i => PdfiumDrawings.IsGeometryEditable(page, i)));
                    Assert.All(indices, i => Assert.Equal(FpdfConstants.AnnotFlagPrint, FlagsOf(page, i)));
                }

                return result;
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(6, editable.Count);
        Assert.All(editable, Assert.True);
    }

    [Fact]
    public async Task ArrowOnTheRotatedPageReadsBackPrecisely()
    {
        using var folder = new TempFolder();
        var copy = Path.Combine(folder.Path, "copy.pdf");
        File.Copy(TestData.Pdf(DrawingsForeign), copy);
        var start = new DisplayPoint(40.3, 210.7);
        var end = new DisplayPoint(197.9, 63.1);
        var strokes = ArrowGeometry.Strokes(start, end, 4, head: true);
        var spec = Spec(PdfiumDrawingKind.Arrow, strokes, 4);
        var saved = Path.Combine(folder.Path, "saved.pdf");
        var thread = PdfiumThread.Shared;
        var (inMemory, reopened) = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, copy, null);
                int index;
                using (var page = document.LoadPage(1))
                {
                    index = PdfiumDrawings.Create(page, spec, (220, 30, 30), "Look", "Ada", "00112233445566778899aabbccddeeff", "D:20261025120000Z");
                }

                Assert.True(PdfiumAnnotations.GenerateAppearance(document, 1, index));
                PdfiumDrawing? inMemory;
                using (var page = document.LoadPage(1))
                {
                    inMemory = PdfiumDrawings.ReadGeometry(page, index);
                }

                using (var stream = new FileStream(saved, FileMode.CreateNew))
                {
                    document.SaveIncremental(stream);
                }

                using var again = PdfiumDocument.Open(thread, saved, null);
                using var reloaded = again.LoadPage(1);
                return (inMemory, PdfiumDrawings.ReadGeometry(reloaded, index));
            },
            cancellationToken: TestContext.Current.CancellationToken);

        foreach (var drawing in new[] { inMemory, reopened })
        {
            Assert.NotNull(drawing);
            Assert.Equal(PdfiumDrawingKind.Arrow, drawing.Kind);
            Assert.Equal(4, drawing.Width);
            Assert.Equal(2, drawing.Strokes.Count);
            for (var i = 0; i < strokes.Count; i++)
            {
                AssertPoints(strokes[i].Select(p => (p.X, p.Y)).ToList(), drawing.Strokes[i], 1e-4);
            }

            var read = drawing.Strokes.Select(s => (IReadOnlyList<DisplayPoint>)s.Select(p => new DisplayPoint(p.X, p.Y)).ToList()).ToList();
            Assert.True(ArrowGeometry.TryRecognize(read, out _, out _, out var head));
            Assert.True(head);
        }
    }

    [Fact]
    public async Task CreatedDrawingsCarryTheirAttributes()
    {
        using var folder = new TempFolder();
        var copy = Path.Combine(folder.Path, "copy.pdf");
        File.Copy(TestData.Pdf("text-basic.pdf"), copy);
        var line = Spec(PdfiumDrawingKind.Line, ArrowGeometry.Strokes(new(20, 20), new(120, 70), 1, head: false), 1);
        var ink = Spec(PdfiumDrawingKind.Ink, [[new(20, 100), new(40, 120), new(60, 100)]], 2);
        var square = new PdfiumDrawing(PdfiumDrawingKind.Square, [], (150, 20, 250, 80), 4);
        var circle = new PdfiumDrawing(PdfiumDrawingKind.Circle, [], (150, 100, 230, 160), 2);
        var thread = PdfiumThread.Shared;
        var results = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, copy, null);
                using var page = document.LoadPage(0);
                var before = Fpdf.FPDFPage_GetAnnotCount(page.Access());
                var list = new List<(PdfiumDrawing? Drawing, PdfiumAnnotation? Annotation, bool Editable)>();
                foreach (var spec in new[] { line, ink, square, circle })
                {
                    var index = PdfiumDrawings.Create(page, spec, (30, 80, 220), "Note", "Ada", Guid.NewGuid().ToString("N"), "D:20261025120000Z");
                    Assert.Equal(before + list.Count, index);
                    list.Add((PdfiumDrawings.ReadGeometry(page, index), PdfiumAnnotations.ReadAt(page, index), PdfiumDrawings.IsGeometryEditable(page, index)));
                }

                return list;
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([PdfiumDrawingKind.Line, PdfiumDrawingKind.Ink, PdfiumDrawingKind.Square, PdfiumDrawingKind.Circle], results.Select(r => r.Drawing!.Kind));
        Assert.Equal([1.0, 2, 4, 2], results.Select(r => r.Drawing!.Width));
        Assert.All(results, r => Assert.True(r.Editable));
        Assert.All(results, r => Assert.Equal((byte)30, r.Annotation!.Color!.Value.R));
        Assert.All(results, r => Assert.Equal("Note", r.Annotation!.Contents));
        Assert.All(results, r => Assert.Equal("Ada", r.Annotation!.Author));
        Assert.All(results, r => Assert.Equal("D:20261025120000Z", r.Annotation!.Modified));
        AssertBox((150, 20, 250, 80), results[2].Drawing!.Box, 1e-4);
        AssertBox((150, 20, 250, 80), results[2].Annotation!.Bounds, 1.0 / 16);
        AssertBox((150, 100, 230, 160), results[3].Drawing!.Box, 1e-4);
        AssertPoints([(20, 100), (40, 120), (60, 100)], results[1].Drawing!.Strokes[0], 1e-4);
    }

    [Fact]
    public async Task InkRectStaysWhenTheAppearanceIsRegenerated()
    {
        using var folder = new TempFolder();
        var copy = Path.Combine(folder.Path, "copy.pdf");
        File.Copy(TestData.Pdf("text-basic.pdf"), copy);
        const double Width = 4;

        // A right angle at (60, 50): its miter reaches sqrt(2) * w / 2 beyond the corner.
        var ink = Spec(PdfiumDrawingKind.Ink, [[new(20, 90), new(60, 50), new(100, 90)]], Width);
        var arrow = Spec(PdfiumDrawingKind.Arrow, ArrowGeometry.Strokes(new(120, 120), new(220, 120), Width, head: true), Width);
        var square = new PdfiumDrawing(PdfiumDrawingKind.Square, [], (150, 20, 250, 80), Width);
        var thread = PdfiumThread.Shared;
        var rects = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, copy, null);
                var indices = new List<int>();
                using (var page = document.LoadPage(0))
                {
                    foreach (var spec in new[] { ink, arrow, square })
                    {
                        indices.Add(PdfiumDrawings.Create(page, spec, (220, 30, 30), null, null, Guid.NewGuid().ToString("N"), "D:20261025120000Z"));
                    }
                }

                var rects = new List<List<(double, double, double, double)>>();
                Assert.True(PdfiumAnnotations.GenerateAppearance(document, 0, indices[0]));
                rects.Add(BoundsOf(document, indices));
                Assert.True(PdfiumAnnotations.GenerateAppearance(document, 0, indices[0]));
                rects.Add(BoundsOf(document, indices));
                using (var page = document.LoadPage(0))
                {
                    foreach (var index in indices)
                    {
                        PdfiumAnnotations.Update(page, index, null, (30, 80, 220), Guid.NewGuid().ToString("N"), "D:20261025130000Z");
                    }
                }

                Assert.True(PdfiumAnnotations.GenerateAppearance(document, 0, indices[0]));
                rects.Add(BoundsOf(document, indices));
                return rects;
            },
            cancellationToken: TestContext.Current.CancellationToken);

        // After PDFium's generator: the hull plus half the width plus the miter beyond it.
        var miter = Width / 2 * Math.Sqrt(2);
        AssertBox((20 - miter, 50 - miter, 100 + miter, 90 + miter), rects[0][0], 1.0 / 16);
        var head = ArrowGeometry.Strokes(new(120, 120), new(220, 120), Width, head: true)[1];
        var tip = Width * ArrowMiter;
        AssertBox((120 - tip, head.Min(p => p.Y) - tip, 220 + tip, head.Max(p => p.Y) + tip), rects[0][1], 1.0 / 16);
        AssertBox((150, 20, 250, 80), rects[0][2], 1.0 / 16);
        Assert.Equal(rects[0], rects[1]);
        Assert.Equal(rects[0], rects[2]);
    }

    [Fact]
    public async Task CreateRefusesLimitsAndLeavesNothingBehind()
    {
        using var folder = new TempFolder();
        var copy = Path.Combine(folder.Path, "copy.pdf");
        File.Copy(TestData.Pdf("text-basic.pdf"), copy);
        IReadOnlyList<DisplayPoint> two = [new(10, 10), new(20, 20)];
        var tooManyStrokes = Spec(PdfiumDrawingKind.Ink, Enumerable.Repeat(two, PdfiumDrawings.MaxInkStrokes + 1).ToList(), 2);
        var tooManyPoints = Spec(PdfiumDrawingKind.Ink, [Enumerable.Range(0, PdfiumDrawings.MaxInkPoints + 1).Select(i => new DisplayPoint(i % 100, i % 50)).ToList()], 2);
        var notFinite = Spec(PdfiumDrawingKind.Ink, [[new(10, 10), new(double.NaN, 20)]], 2);
        var empty = Spec(PdfiumDrawingKind.Ink, [], 2);
        var onePoint = Spec(PdfiumDrawingKind.Ink, [two, [new(30, 30)]], 2);
        var noWidth = Spec(PdfiumDrawingKind.Ink, [two], 0);
        var flatSquare = new PdfiumDrawing(PdfiumDrawingKind.Square, [], (10, 10, 10, 50), 2);
        var thread = PdfiumThread.Shared;
        var counts = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, copy, null);
                using var page = document.LoadPage(0);
                var before = Fpdf.FPDFPage_GetAnnotCount(page.Access());
                Assert.Throws<ArgumentOutOfRangeException>(() => Create(page, tooManyStrokes));
                Assert.Throws<ArgumentOutOfRangeException>(() => Create(page, tooManyPoints));
                Assert.ThrowsAny<ArgumentException>(() => Create(page, notFinite));
                Assert.ThrowsAny<ArgumentException>(() => Create(page, empty));
                Assert.ThrowsAny<ArgumentException>(() => Create(page, onePoint));
                Assert.ThrowsAny<ArgumentException>(() => Create(page, noWidth));
                Assert.ThrowsAny<ArgumentException>(() => Create(page, flatSquare));
                return (before, Fpdf.FPDFPage_GetAnnotCount(page.Access()));
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(counts.before, counts.Item2);

        static int Create(PdfiumPage page, PdfiumDrawing spec) =>
            PdfiumDrawings.Create(page, spec, (0, 0, 0), null, null, Guid.NewGuid().ToString("N"), "D:20261025120000Z");
    }

    [Fact]
    public void IsDrawingNamesInkSquareAndCircle()
    {
        Assert.True(PdfiumAnnotations.IsDrawing(FpdfConstants.AnnotInk));
        Assert.True(PdfiumAnnotations.IsDrawing(FpdfConstants.AnnotSquare));
        Assert.True(PdfiumAnnotations.IsDrawing(FpdfConstants.AnnotCircle));
        Assert.False(PdfiumAnnotations.IsDrawing(FpdfConstants.AnnotHighlight));
        Assert.False(PdfiumAnnotations.IsDrawing(4));
    }

    [Theory]
    [InlineData("1 0 0 RG 2 w 30 240 m 60 280 l S", 2.0)]
    [InlineData("q /GS gs (3 w) Tj [4 2] 0 d 0.5 w 0 0 m S Q", 0.5)]
    [InlineData("1 0 0 RG 0 0 m 10 10 l S", null)]
    [InlineData("-1 w 2.5 w", 2.5)]
    public void WidthFromAppearanceFindsTheFirstLineWidth(string content, double? expected) =>
        Assert.Equal(expected, PdfiumDrawings.WidthFromAppearance(content));

    private static PdfiumDrawing Spec(PdfiumDrawingKind kind, IReadOnlyList<IReadOnlyList<DisplayPoint>> strokes, double width) =>
        new(kind, strokes.Select(s => (IReadOnlyList<(double X, double Y)>)s.Select(p => (p.X, p.Y)).ToList()).ToList(), default, width);

    private static List<(double, double, double, double)> BoundsOf(PdfiumDocument document, List<int> indices)
    {
        using var page = document.LoadPage(0);
        return indices.Select(i => PdfiumAnnotations.ReadAt(page, i)!.Bounds).ToList();
    }

    private static async Task<string> DeriveHostileInkAsync(TempFolder folder, string name, string strokes)
    {
        var fixture = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(TestData.Pdf(DrawingsForeign), TestContext.Current.CancellationToken));
        const string Original = "/InkList [[30 240 60 280 90 240] [110 240 150 280 190 240]]";
        Assert.Contains(Original, fixture, StringComparison.Ordinal);
        var path = Path.Combine(folder.Path, name);
        await File.WriteAllBytesAsync(path, Encoding.Latin1.GetBytes(fixture.Replace(Original, $"/InkList [{strokes}]", StringComparison.Ordinal)), TestContext.Current.CancellationToken);
        return path;
    }

    private static async Task<(PdfiumDrawing? Drawing, bool Editable)> ReadWithEditableAsync(string path)
    {
        var thread = PdfiumThread.Shared;
        return await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, path, null);
                using var page = document.LoadPage(0);
                return (PdfiumDrawings.ReadGeometry(page, 0), PdfiumDrawings.IsGeometryEditable(page, 0));
            },
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private static async Task<PdfiumDrawing?> ReadAsync(string path, int pageIndex, int index)
    {
        var thread = PdfiumThread.Shared;
        return await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, path, null);
                using var page = document.LoadPage(pageIndex);
                return PdfiumDrawings.ReadGeometry(page, index);
            },
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private static int FlagsOf(PdfiumPage page, int index)
    {
        var annotation = Fpdf.FPDFPage_GetAnnot(page.Access(), index);
        Assert.NotEqual(0, annotation);
        try
        {
            return Fpdf.FPDFAnnot_GetFlags(annotation);
        }
        finally
        {
            Fpdf.FPDFPage_CloseAnnot(annotation);
        }
    }

    private static async Task<bool> IsGeometryEditableAsync(string path, int pageIndex, int index)
    {
        var thread = PdfiumThread.Shared;
        return await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, path, null);
                using var page = document.LoadPage(pageIndex);
                return PdfiumDrawings.IsGeometryEditable(page, index);
            },
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private static void AssertPoints(List<(double X, double Y)> expected, IReadOnlyList<(double X, double Y)> actual, double tolerance)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.True(
                Math.Abs(expected[i].X - actual[i].X) <= tolerance && Math.Abs(expected[i].Y - actual[i].Y) <= tolerance,
                $"Point {i}: expected {expected[i]}, got {actual[i]} (tolerance {tolerance}).");
        }
    }

    private static void AssertBox((double Left, double Top, double Right, double Bottom) expected, (double Left, double Top, double Right, double Bottom) actual, double tolerance) =>
        Assert.True(
            Math.Abs(expected.Left - actual.Left) <= tolerance && Math.Abs(expected.Top - actual.Top) <= tolerance
            && Math.Abs(expected.Right - actual.Right) <= tolerance && Math.Abs(expected.Bottom - actual.Bottom) <= tolerance,
            $"Expected {expected}, got {actual} (tolerance {tolerance}).");
}

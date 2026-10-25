using System.Text;
using System.Text.RegularExpressions;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;
using Pagewing.Core.Text;
using Pagewing.TestSupport;
using Xunit;
using static Pagewing.Core.Tests.Annotations.AnnotationTestFiles;

namespace Pagewing.Core.Tests.Annotations;

/// <summary>Creating, replacing, saving and reading drawings through <see cref="PdfDocument"/> (milestone 10).</summary>
public sealed partial class DrawingRoundTripTests : IDisposable
{
    private const double Scale = 4;

    private static readonly AnnotationColor Red = new(220, 30, 30);

    private static readonly DateTimeOffset Created = new(2026, 10, 25, 8, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Moved = new(2026, 10, 25, 9, 30, 0, TimeSpan.Zero);

    private readonly TempFolder folder = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => folder.Dispose();

    [Fact]
    public async Task InkSurvivesSaveAndReload()
    {
        IReadOnlyList<DisplayPoint>[] strokes =
        [
            [new(20, 30), new(40, 60.25), new(70.5, 35), new(90, 80)],
            [new(120, 40), new(160, 41.75)],
        ];
        var geometry = DrawingGeometry.Ink(strokes, 2);
        string saved;
        PdfAnnotation created;
        await using (var document = await OpenCopyAsync("text-basic.pdf"))
        {
            created = await document.CreateDrawingAsync(0, geometry, Red, "A note", "  Ada  ", Ct);
            Assert.Equal(AnnotationKind.Ink, created.Kind);
            Assert.Equal("Ink", created.Subtype);
            Assert.Matches("^[0-9a-f]{32}$", created.Name);
            Assert.Equal("Ada", created.Author);
            Assert.Equal("A note", created.Contents);
            Assert.Equal(Red, created.Color);
            Assert.True(created.Editable);
            Assert.True(created.GeometryEditable);
            AssertGeometry(geometry, created.Drawing, 1e-4);
            saved = await SaveAsync(document, folder);
        }

        await using var reopened = await OpenAsync(saved);
        var annotation = Assert.Single(await reopened.GetAnnotationsAsync(0, Ct));
        Assert.Equal(created.Name, annotation.Name);
        Assert.Equal(AnnotationKind.Ink, annotation.Kind);
        Assert.Equal(Red, annotation.Color);
        Assert.Equal("A note", annotation.Contents);
        Assert.Equal("Ada", annotation.Author);
        Assert.True(annotation.Editable);
        Assert.True(annotation.GeometryEditable);
        AssertGeometry(geometry, annotation.Drawing, 1e-4);
    }

    [Fact]
    public async Task RectangleAndEllipseSurvive()
    {
        var rectangle = DrawingGeometry.Rectangle(new PageRect(150, 20, 250, 80), 4);
        var ellipse = DrawingGeometry.Ellipse(new PageRect(160.5, 100, 230, 160.25), 1);
        string saved;
        await using (var document = await OpenCopyAsync("text-basic.pdf"))
        {
            await document.CreateDrawingAsync(0, rectangle, Blue, null, null, Ct);
            await document.CreateDrawingAsync(0, ellipse, Red, "round", null, Ct);
            saved = await SaveAsync(document, folder);
        }

        await using var reopened = await OpenAsync(saved);
        var annotations = await reopened.GetAnnotationsAsync(0, Ct);
        Assert.Equal([AnnotationKind.Rectangle, AnnotationKind.Ellipse], annotations.Select(a => a.Kind));
        Assert.Equal(["Square", "Circle"], annotations.Select(a => a.Subtype));
        Assert.Equal([Blue, Red], annotations.Select(a => a.Color));
        Assert.Null(annotations[0].Contents);
        Assert.Equal("round", annotations[1].Contents);
        AssertGeometry(rectangle, annotations[0].Drawing, 1e-3);
        AssertGeometry(ellipse, annotations[1].Drawing, 1e-3);

        // The stroke lies inside the box, so the /Rect is the box itself.
        AssertRectNear(rectangle.Box, annotations[0].Bounds, 1.0 / 16);
        AssertRectNear(ellipse.Box, annotations[1].Bounds, 1.0 / 16);
        Assert.All(annotations, a => Assert.True(a.GeometryEditable));
    }

    [Fact]
    public async Task ArrowIsRecognisedAfterReload()
    {
        // Unrotated, and on the page with /Rotate 90 and a CropBox offset, where any rounding of the conversion would turn the head.
        await ArrowSurvivesAsync("text-basic.pdf", 0, new DisplayPoint(30.3, 150.7), new DisplayPoint(250.9, 40.1));
        await ArrowSurvivesAsync(DrawingsForeign, 1, new DisplayPoint(40.3, 210.7), new DisplayPoint(197.9, 63.1));
    }

    [Fact]
    public async Task AppearanceIsAFormXObjectWithPrintFlag()
    {
        var copy = Copy("text-basic.pdf");
        var original = await File.ReadAllBytesAsync(copy, Ct);
        var names = new Dictionary<AnnotationKind, string>();
        string saved;
        await using (var document = await OpenAsync(copy))
        {
            foreach (var geometry in new[]
            {
                DrawingGeometry.Ink([[new(20, 20), new(40, 50), new(60, 20)]], 2),
                DrawingGeometry.Rectangle(new PageRect(100, 20, 160, 60), 4),
                DrawingGeometry.Ellipse(new PageRect(180, 20, 260, 60), 2),
                DrawingGeometry.Line(new DisplayPoint(20, 100), new DisplayPoint(120, 140), 1),
                DrawingGeometry.Arrow(new DisplayPoint(150, 100), new DisplayPoint(260, 160), 4),
            })
            {
                names[geometry.Kind] = (await document.CreateDrawingAsync(0, geometry, Red, null, null, Ct)).Name!;
            }

            saved = await SaveAsync(document, folder);
        }

        var appended = Appended(original, await File.ReadAllBytesAsync(saved, Ct));
        foreach (var (kind, name) in names)
        {
            var dictionary = AnnotationDictionary(appended, name);
            Assert.Matches(FlagPrintPattern(), dictionary);
            Assert.Matches(FormPattern(), AppearanceObject(appended, dictionary));
            Assert.Matches(BorderPattern(), dictionary);
            Assert.DoesNotContain("/IC", dictionary, StringComparison.Ordinal);
            Assert.DoesNotContain("/BS", dictionary, StringComparison.Ordinal);
            switch (kind)
            {
                case AnnotationKind.Arrow:
                    Assert.Contains("/Subj(Arrow)", dictionary, StringComparison.Ordinal);
                    break;
                case AnnotationKind.Line:
                    Assert.Contains("/Subj(Line)", dictionary, StringComparison.Ordinal);
                    break;
                default:
                    Assert.DoesNotContain("/Subj", dictionary, StringComparison.Ordinal);
                    break;
            }
        }
    }

    [Fact]
    public Task RotatedPageKeepsPointsOnTheirPlace() => KeepsPointsOnTheirPlaceAsync("rotated-page.pdf");

    [Fact]
    public Task CropBoxOffsetKeepsPointsOnTheirPlace() => KeepsPointsOnTheirPlaceAsync("cropbox.pdf");

    [Fact]
    public async Task MoveKeepsNameCommentAndAuthor()
    {
        var copy = Copy("text-basic.pdf");
        var original = await File.ReadAllBytesAsync(copy, Ct);
        var geometry = DrawingGeometry.Ink([[new(20, 30), new(40, 60), new(70, 35)]], 2);
        PdfAnnotation moved;
        DrawingGeometry target;
        string saved;
        await using (var document = await OpenAsync(copy))
        {
            document.Clock = new FixedClock(Created);
            var created = await document.CreateDrawingAsync(0, geometry, Red, "Keep me", "Ada", Ct);
            var size = await document.GetPageSizeAsync(0, Ct);
            target = DrawingTransform.Move(created.Drawing!, 30, 20, new PageRect(0, 0, size.Width, size.Height));

            document.Clock = new FixedClock(Moved);
            moved = await document.ReplaceDrawingAsync(new AnnotationLocator(0, created.Index, created.Name), target, Red, Ct);

            Assert.Equal(created.Name, moved.Name);
            Assert.Equal("Keep me", moved.Contents);
            Assert.Equal("Ada", moved.Author);
            Assert.Equal(Red, moved.Color);
            Assert.Equal(Moved, moved.Modified);
            AssertGeometry(target, moved.Drawing, 1e-4);
            var listed = Assert.Single(await document.GetAnnotationsAsync(0, Ct));
            Assert.Equal(moved.Index, listed.Index);
            Assert.Equal(created.Name, listed.Name);
            saved = await SaveAsync(document, folder);
        }

        var dictionary = AnnotationDictionary(Appended(original, await File.ReadAllBytesAsync(saved, Ct)), moved.Name!);
        Assert.Contains("/CreationDate(D:20261025080000Z)", dictionary, StringComparison.Ordinal);
        Assert.Contains("/M(D:20261025093000Z)", dictionary, StringComparison.Ordinal);
        await using var reopened = await OpenAsync(saved);
        var annotation = Assert.Single(await reopened.GetAnnotationsAsync(0, Ct));
        Assert.Equal(moved.Name, annotation.Name);
        Assert.Equal("Keep me", annotation.Contents);
        Assert.Equal("Ada", annotation.Author);
        AssertGeometry(target, annotation.Drawing, 1e-4);
    }

    [Fact]
    public async Task UpdateChangesColorWidthAndCommentOfADrawing()
    {
        string saved;
        string name;
        var arrow = DrawingGeometry.Arrow(new DisplayPoint(40, 150), new DisplayPoint(220, 50), 2);
        var wider = DrawingGeometry.Arrow(new DisplayPoint(40, 150), new DisplayPoint(220, 50), 4);
        await using (var document = await OpenCopyAsync("text-basic.pdf"))
        {
            var created = await document.CreateDrawingAsync(0, arrow, Red, "first", null, Ct);
            name = created.Name!;

            var recoloured = await document.UpdateAnnotationAsync(new AnnotationLocator(0, created.Index, name), new AnnotationEdit(Color: Blue), Ct);
            Assert.Equal(name, recoloured.Name);
            Assert.Equal(Blue, recoloured.Color);
            Assert.Equal("first", recoloured.Contents);
            Assert.Equal(AnnotationKind.Arrow, recoloured.Kind);
            AssertGeometry(arrow, recoloured.Drawing, 1e-4);

            var widened = await document.UpdateAnnotationAsync(new AnnotationLocator(0, recoloured.Index, name), new AnnotationEdit(Contents: "second", Geometry: wider), Ct);
            Assert.Equal(Blue, widened.Color);
            Assert.Equal("second", widened.Contents);
            AssertGeometry(wider, widened.Drawing, 1e-4);

            var commented = await document.UpdateAnnotationAsync(new AnnotationLocator(0, widened.Index, name), new AnnotationEdit(Contents: "third"), Ct);
            Assert.Equal("third", commented.Contents);
            AssertGeometry(wider, commented.Drawing, 1e-4);
            Assert.Single(await document.GetAnnotationsAsync(0, Ct));

            // A markup has no geometry to replace, and a drawing keeps its kind.
            var markup = await document.CreateMarkupAsync(0, AnnotationKind.Highlight, [new PageRect(20, 20, 60, 40)], Yellow, null, null, Ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() => document.UpdateAnnotationAsync(new AnnotationLocator(0, markup.Index, markup.Name), new AnnotationEdit(Geometry: wider), Ct));
            await Assert.ThrowsAsync<ArgumentException>(() => document.ReplaceDrawingAsync(new AnnotationLocator(0, commented.Index, name), DrawingGeometry.Rectangle(new PageRect(10, 10, 50, 50), 2), Red, Ct));
            saved = await SaveAsync(document, folder);
        }

        await using var reopened = await OpenAsync(saved);
        var annotation = (await reopened.GetAnnotationsAsync(0, Ct)).Single(a => a.Name == name);
        Assert.Equal(AnnotationKind.Arrow, annotation.Kind);
        Assert.Equal(Blue, annotation.Color);
        Assert.Equal("third", annotation.Contents);
        AssertGeometry(wider, annotation.Drawing, 1e-4);
    }

    [Fact]
    public async Task ReplaceRestoresTheOldOneWhenCreationFails()
    {
        var geometry = DrawingGeometry.Ink([[new(20, 30), new(40, 60), new(70, 35)]], 2);
        await using var document = await OpenCopyAsync("text-basic.pdf");
        var created = await document.CreateDrawingAsync(0, geometry, Red, "Stay", "Ada", Ct);
        var locator = new AnnotationLocator(0, created.Index, created.Name);

        // PDFium's side refuses a point beyond its largest coordinate, after the checks of the document passed.
        var far = DrawingGeometry.Ink([[new(20, 30), new(2e7, 60)]], 2);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => document.ReplaceDrawingAsync(locator, far, Blue, Ct));
        await AssertUnchangedAsync();

        // The new drawing is on the page, but PDFium builds no appearance for it.
        document.AppearanceGenerator = (_, _, _) => false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => document.ReplaceDrawingAsync(locator, DrawingGeometry.Ink([[new(30, 30), new(50, 60)]], 2), Blue, Ct));
        await AssertUnchangedAsync();

        // The appearance render fails.
        document.AppearanceGenerator = (_, _, _) => throw new Pagewing.Pdfium.PdfiumRenderException("injected");
        await Assert.ThrowsAsync<InvalidOperationException>(() => document.UpdateAnnotationAsync(locator, new AnnotationEdit(Color: Blue), Ct));
        await AssertUnchangedAsync();

        // Creating fails the same way and leaves nothing behind.
        await Assert.ThrowsAsync<InvalidOperationException>(() => document.CreateDrawingAsync(0, geometry, Blue, null, null, Ct));
        await AssertUnchangedAsync();

        async Task AssertUnchangedAsync()
        {
            var annotation = Assert.Single(await document.GetAnnotationsAsync(0, Ct));
            Assert.Equal(created.Name, annotation.Name);
            Assert.Equal(created.Index, annotation.Index);
            Assert.Equal("Stay", annotation.Contents);
            Assert.Equal("Ada", annotation.Author);
            Assert.Equal(Red, annotation.Color);
            AssertGeometry(geometry, annotation.Drawing, 1e-4);
        }
    }

    [Fact]
    public async Task ForeignWithOwnAppearanceCannotBeReplaced()
    {
        await using var document = await OpenCopyAsync(DrawingsForeign);
        var annotations = await document.GetAnnotationsAsync(0, Ct);

        // ink-0001: two strokes and its own appearance. Listed with its geometry, deletable and commentable, not movable.
        var ink = annotations.Single(a => a.Name == "ink-0001");
        Assert.Equal(AnnotationKind.Ink, ink.Kind);
        Assert.True(ink.Editable);
        Assert.False(ink.GeometryEditable);
        Assert.NotNull(ink.Drawing);
        Assert.Equal(2, ink.Drawing.Strokes.Count);
        var locator = new AnnotationLocator(0, ink.Index, ink.Name);
        var moved = DrawingTransform.Move(ink.Drawing, 10, 10, new PageRect(0, 0, 400, 300));
        await Assert.ThrowsAsync<InvalidOperationException>(() => document.ReplaceDrawingAsync(locator, moved, Red, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => document.UpdateAnnotationAsync(locator, new AnnotationEdit(Color: Blue), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => document.UpdateAnnotationAsync(locator, new AnnotationEdit(Geometry: moved), Ct));
        var commented = await document.UpdateAnnotationAsync(locator, new AnnotationEdit(Contents: "Seen"), Ct);
        Assert.Equal("Seen", commented.Contents);
        AssertGeometry(ink.Drawing, commented.Drawing, 1e-4);

        // The other foreign drawings: kinds as read, all locked for the geometry (/BS, /BE, /CA, a reply), the one with a reply read-only.
        Assert.Equal(
            [AnnotationKind.Ink, AnnotationKind.Ink, AnnotationKind.Rectangle, AnnotationKind.Rectangle, AnnotationKind.Rectangle, AnnotationKind.Ellipse, AnnotationKind.Other, AnnotationKind.Ink],
            annotations.Select(a => a.Kind));
        Assert.All(annotations, a => Assert.False(a.GeometryEditable));
        Assert.Equal([true, true, true, true, true, true, false, false], annotations.Select(a => a.Editable));
        var answered = annotations.Single(a => a.Name == "ink-0008");
        await Assert.ThrowsAsync<InvalidOperationException>(() => document.DeleteAnnotationAsync(new AnnotationLocator(0, answered.Index, answered.Name), Ct));

        await document.DeleteAnnotationAsync(locator, Ct);
        Assert.DoesNotContain(await document.GetAnnotationsAsync(0, Ct), a => a.Name == "ink-0001");
    }

    [Fact]
    public async Task ExistingBytesStayThePrefix()
    {
        var path = Copy(DrawingsForeign);
        var before = await File.ReadAllBytesAsync(path, Ct);
        var engine = new PdfEngine();
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var stamp = FileStamp.Of(path);
        var ink = await document.CreateDrawingAsync(0, DrawingGeometry.Ink([[new(20, 200), new(60, 240), new(90, 210)]], 2), Red, "new", "Ada", Ct);
        var arrow = await document.CreateDrawingAsync(1, DrawingGeometry.Arrow(new DisplayPoint(30, 300), new DisplayPoint(200, 250), 2), Blue, null, null, Ct);
        var moved = await document.ReplaceDrawingAsync(
            new AnnotationLocator(0, ink.Index, ink.Name), DrawingTransform.Move(ink.Drawing!, 5, -5, new PageRect(0, 0, 400, 300)), Red, Ct);
        await document.DeleteAnnotationAsync(new AnnotationLocator(0, 1, "ink-0002"), Ct);
        var counts = new Dictionary<int, int>
        {
            [0] = (await document.GetAnnotationsAsync(0, Ct)).Count,
            [1] = (await document.GetAnnotationsAsync(1, Ct)).Count,
        };
        Assert.Equal([8, 2], counts.Values);
        var request = new SaveRequest(path, path, stamp, null, document.PageCount, counts, OverwriteChangedOriginal: false);

        var result = await DocumentSaver.SaveAsync(document, engine, request, cancellationToken: Ct);
        await using (result.Reopened)
        {
            var after = await File.ReadAllBytesAsync(path, Ct);
            Assert.True(after.Length > before.Length);
            Assert.True(after.AsSpan(0, before.Length).SequenceEqual(before), "The original bytes were changed.");
            var first = await result.Reopened.GetAnnotationsAsync(0, Ct);
            Assert.DoesNotContain(first, a => a.Name == "ink-0002");
            AssertGeometry(moved.Drawing!, first.Single(a => a.Name == ink.Name).Drawing, 1e-4);
            var second = await result.Reopened.GetAnnotationsAsync(1, Ct);
            Assert.Equal(AnnotationKind.Arrow, second.Single(a => a.Name == arrow.Name).Kind);
        }
    }

    [Fact]
    public async Task LimitIsReported()
    {
        // One page with as many annotations as the document lists: a new one would never be listed, so it is refused.
        var full = WriteNotes("full.pdf", PdfDocument.MaxAnnotations, 0);
        var geometry = DrawingGeometry.Ink([[new(20, 30), new(40, 60)]], 2);
        await using (var document = await OpenAsync(full))
        {
            await Assert.ThrowsAsync<PdfAnnotationLimitException>(() => document.CreateDrawingAsync(0, geometry, Red, null, null, Ct));
            await Assert.ThrowsAsync<PdfAnnotationLimitException>(() => document.CreateMarkupAsync(0, AnnotationKind.Highlight, [new PageRect(20, 20, 60, 40)], Yellow, null, null, Ct));
            Assert.Equal(PdfDocument.MaxAnnotations, (await document.GetAnnotationsAsync(0, Ct)).Count);
        }

        // Spread over two pages: the listed page counts, the last free place is taken and then the limit is reached.
        var spread = WriteNotes("spread.pdf", 6_000, PdfDocument.MaxAnnotations - 6_000 - 1);
        await using (var document = await OpenAsync(spread))
        {
            Assert.Equal(6_000, (await document.GetAnnotationsAsync(0, Ct)).Count);
            var last = await document.CreateDrawingAsync(1, geometry, Red, null, null, Ct);
            await Assert.ThrowsAsync<PdfAnnotationLimitException>(() => document.CreateDrawingAsync(1, geometry, Red, null, null, Ct));
            Assert.Contains(await document.GetAnnotationsAsync(1, Ct), a => a.Name == last.Name);

            // Replacing keeps the number of annotations and stays possible at the limit.
            var moved = await document.ReplaceDrawingAsync(new AnnotationLocator(1, last.Index, last.Name), DrawingTransform.Move(last.Drawing!, 10, 10, new PageRect(0, 0, 300, 300)), Blue, Ct);
            Assert.Equal(Blue, moved.Color);
            Assert.Equal(PdfDocument.MaxAnnotations - 6_000, (await document.GetAnnotationsAsync(1, Ct)).Count);
        }
    }

    [Fact]
    public async Task CreateRejectsEmptyAndOversizedDrawings()
    {
        await using var document = await OpenCopyAsync("text-basic.pdf");
        IReadOnlyList<DisplayPoint> two = [new(10, 10), new(20, 20)];

        await Assert.ThrowsAsync<ArgumentException>(() => document.CreateDrawingAsync(0, DrawingGeometry.Ink([[new(10, 10)]], 2), Red, null, null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => document.CreateDrawingAsync(0, DrawingGeometry.Rectangle(new PageRect(10, 10, 10, 50), 2), Red, null, null, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.CreateDrawingAsync(0, DrawingGeometry.Ink([two], 0), Red, null, null, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.CreateDrawingAsync(0, DrawingGeometry.Ink([two], 1_001), Red, null, null, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.CreateDrawingAsync(0, DrawingGeometry.Ink(Enumerable.Repeat(two, 1_001), 2), Red, null, null, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.CreateDrawingAsync(5, DrawingGeometry.Ink([two], 2), Red, null, null, Ct));

        // A stroke of one point draws nothing and is left out; the others are created.
        var created = await document.CreateDrawingAsync(0, DrawingGeometry.Ink([two, [new(40, 40)]], 2), Red, null, null, Ct);
        Assert.Single(created.Drawing!.Strokes);
        Assert.Single(await document.GetAnnotationsAsync(0, Ct));
    }

    private static async Task ArrowSurvivesAsync(string file, int pageIndex, DisplayPoint start, DisplayPoint end)
    {
        using var folder = new TempFolder();
        var copy = Path.Combine(folder.Path, "copy.pdf");
        File.Copy(TestData.Pdf(file), copy);
        var arrow = DrawingGeometry.Arrow(start, end, 4);
        var line = DrawingGeometry.Line(end, start, 1);

        // Strokes of an arrow's shape, but created as freehand: no marker, so freehand; and the marker of an arrow on strokes of another shape is freehand too.
        var freehand = DrawingGeometry.Ink(arrow.Strokes, 4);
        var fake = DrawingGeometry.FromStrokes(AnnotationKind.Arrow, [[start, new DisplayPoint(start.X + 20, start.Y + 3), end]], 2);
        string saved;
        await using (var document = await OpenAsync(copy))
        {
            var created = await document.CreateDrawingAsync(pageIndex, arrow, Red, null, null, Ct);
            Assert.Equal(AnnotationKind.Arrow, created.Kind);
            await document.CreateDrawingAsync(pageIndex, line, Red, null, null, Ct);
            await document.CreateDrawingAsync(pageIndex, freehand, Red, null, null, Ct);
            await document.CreateDrawingAsync(pageIndex, fake, Red, null, null, Ct);
            saved = await SaveAsync(document, folder);
        }

        await using var reopened = await OpenAsync(saved);
        var drawings = (await reopened.GetAnnotationsAsync(pageIndex, Ct)).Where(a => a.Drawing is not null && a.GeometryEditable).ToList();
        Assert.Equal([AnnotationKind.Arrow, AnnotationKind.Line, AnnotationKind.Ink, AnnotationKind.Ink], drawings.Select(a => a.Kind));
        var read = drawings[0].Drawing!;
        Assert.Equal(AnnotationKind.Arrow, read.Kind);
        Assert.Equal(4, read.Width);
        Assert.True(ArrowGeometry.TryRecognize(read.Strokes, out var readStart, out var readEnd, out var head));
        Assert.True(head);
        AssertPointNear(start, readStart, 1e-4);
        AssertPointNear(end, readEnd, 1e-4);
        AssertGeometry(arrow, read, 1e-4);
        Assert.Equal(AnnotationKind.Line, drawings[1].Drawing!.Kind);
        AssertGeometry(line, drawings[1].Drawing, 1e-4);
        AssertGeometry(freehand, drawings[2].Drawing, 1e-4);
        Assert.Equal(AnnotationKind.Ink, drawings[3].Drawing!.Kind);
    }

    /// <summary>A stroke drawn where the pointer was is read back there and painted there by PDFium.</summary>
    private async Task KeepsPointsOnTheirPlaceAsync(string file)
    {
        var ink = DrawingGeometry.Ink([[new(20, 60), new(80, 60)], [new(25, 90), new(50, 120.5), new(75, 95)]], 4);
        var rectangle = DrawingGeometry.Rectangle(new PageRect(15, 20, 85, 45), 2);
        string saved;
        await using (var document = await OpenCopyAsync(file))
        {
            await document.CreateDrawingAsync(0, ink, Red, null, null, Ct);
            await document.CreateDrawingAsync(0, rectangle, Blue, null, null, Ct);
            saved = await SaveAsync(document, folder);
        }

        await using var reopened = await OpenAsync(saved);
        var annotations = await reopened.GetAnnotationsAsync(0, Ct);
        Assert.Equal([AnnotationKind.Ink, AnnotationKind.Rectangle], annotations.Select(a => a.Kind));
        AssertGeometry(ink, annotations[0].Drawing, 1e-4);
        AssertGeometry(rectangle, annotations[1].Drawing, 1e-4);
        AssertRectNear(rectangle.Box, annotations[1].Bounds, 1.0 / 16);

        var image = await reopened.RenderPageAsync(0, Scale, cancellationToken: Ct);
        var stroke = MeanColor(image, Scale, new PageRect(30, 58.5, 70, 61.5));
        Assert.True(stroke.R > 180 && stroke.G < 90 && stroke.B < 90, $"The horizontal stroke is not where it was drawn: {stroke}");
        var edge = MeanColor(image, Scale, new PageRect(30, 20.1, 70, 21.9));
        Assert.True(edge.B > 180 && edge.R < 120, $"The top edge of the rectangle is not where it was drawn: {edge}");
    }

    private static void AssertGeometry(DrawingGeometry expected, DrawingGeometry? actual, double tolerance)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.True(Math.Abs(expected.Width - actual.Width) <= 1e-6, $"Width {actual.Width} instead of {expected.Width}.");
        Assert.Equal(expected.Strokes.Count, actual.Strokes.Count);
        for (var s = 0; s < expected.Strokes.Count; s++)
        {
            Assert.Equal(expected.Strokes[s].Count, actual.Strokes[s].Count);
            for (var i = 0; i < expected.Strokes[s].Count; i++)
            {
                AssertPointNear(expected.Strokes[s][i], actual.Strokes[s][i], tolerance);
            }
        }

        AssertRectNear(expected.Box, actual.Box, tolerance);
    }

    private static void AssertPointNear(DisplayPoint expected, DisplayPoint actual, double tolerance) =>
        Assert.True(
            Math.Abs(expected.X - actual.X) <= tolerance && Math.Abs(expected.Y - actual.Y) <= tolerance,
            $"Expected {expected}, got {actual} (tolerance {tolerance}).");

    private string Copy(string name)
    {
        var path = Path.Combine(folder.Path, "copy-" + name);
        File.Copy(TestData.Pdf(name), path);
        return path;
    }

    private Task<PdfDocument> OpenCopyAsync(string name) => OpenAsync(Copy(name));

    /// <summary>A file of two pages 300 x 300 with the given numbers of notes (direct dictionaries in /Annots).</summary>
    private string WriteNotes(string name, int first, int second)
    {
        static string Notes(int count)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < count; i++)
            {
                builder.Append("<< /Subtype /Text /Rect [10 10 20 20] >> ");
            }

            return builder.ToString();
        }

        return WritePdf(folder, name,
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [{Notes(first)}] >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [{Notes(second)}] >>",
        ]);
    }

    private static string Appended(byte[] original, byte[] saved)
    {
        Assert.True(saved.Length > original.Length);
        Assert.True(saved.AsSpan(0, original.Length).SequenceEqual(original), "The original bytes were changed.");
        return Encoding.Latin1.GetString(saved, original.Length, saved.Length - original.Length);
    }

    /// <summary>The dictionary of the indirect annotation object whose /NM is <paramref name="name"/>.</summary>
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

    [GeneratedRegex(@"/F\s*4(?![\d.])")]
    private static partial Regex FlagPrintPattern();

    [GeneratedRegex(@"/Subtype\s*/Form")]
    private static partial Regex FormPattern();

    [GeneratedRegex(@"/Border\s*\[\s*0\s+0\s+[\d.]+\s*\]")]
    private static partial Regex BorderPattern();
}

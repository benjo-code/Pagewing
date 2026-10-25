using System.Globalization;
using System.Text;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Text;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Annotations;

/// <summary>Helpers of the annotation tests: temp folders, word rectangles, synthetic files and pixel checks.</summary>
internal static class AnnotationTestFiles
{
    public static readonly AnnotationColor Yellow = new(255, 230, 0);

    public static readonly AnnotationColor Blue = new(40, 110, 230);

    /// <summary>Drawings written by other programs (ink, square, circle, line, a reply); see tools/make-testpdfs.cs.</summary>
    public const string DrawingsForeign = "drawings-foreign.pdf";

    public static Task<PdfDocument> OpenAsync(string path) =>
        new PdfEngine().OpenAsync(path, cancellationToken: TestContext.Current.CancellationToken);

    public static Task<PdfDocument> OpenFixtureAsync(string name) => OpenAsync(TestData.Pdf(name));

    /// <summary>The selection rectangles of the first occurrence of <paramref name="word"/> on a page.</summary>
    public static async Task<IReadOnlyList<PageRect>> WordRectsAsync(PdfDocument document, int pageIndex, string word)
    {
        var text = await document.GetPageTextAsync(pageIndex, TestContext.Current.CancellationToken);
        var all = text.GetText(0, text.Chars.Count);
        var start = all.IndexOf(word, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{word}' is not on page {pageIndex}: '{all}'");
        var rects = text.RectsOf(start, start + word.Length);
        Assert.NotEmpty(rects);
        return rects;
    }

    /// <summary>Saves the changes incrementally into <paramref name="folder"/> and returns the new file.</summary>
    public static async Task<string> SaveAsync(PdfDocument document, TempFolder folder, string name = "saved.pdf")
    {
        var path = Path.Combine(folder.Path, name);
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            await document.SaveIncrementalAsync(stream, TestContext.Current.CancellationToken);
        }

        return path;
    }

    /// <summary>Writes a one-file PDF from numbered objects (object 1 is the catalog) into <paramref name="folder"/>.</summary>
    public static string WritePdf(TempFolder folder, string name, IReadOnlyList<string> objects)
    {
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        var path = Path.Combine(folder.Path, name);
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(pdf.ToString()));
        return path;
    }

    /// <summary>The mean colour (R, G, B) of the pixels inside <paramref name="rect"/> (page points), shrunk by one pixel.</summary>
    public static (double R, double G, double B) MeanColor(RenderedImage image, double scale, PageRect rect)
    {
        var x0 = (int)Math.Ceiling(rect.Left * scale) + 1;
        var x1 = (int)Math.Floor(rect.Right * scale) - 1;
        var y0 = (int)Math.Ceiling(rect.Top * scale) + 1;
        var y1 = (int)Math.Floor(rect.Bottom * scale) - 1;
        var pixels = image.Pixels.Span;
        double r = 0, g = 0, b = 0;
        var count = 0;
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var o = (y * image.Stride) + (x * 4);
                b += pixels[o];
                g += pixels[o + 1];
                r += pixels[o + 2];
                count++;
            }
        }

        Assert.True(count > 0, "The rectangle has no pixels.");
        return (r / count, g / count, b / count);
    }

    public static void AssertRectNear(PageRect expected, PageRect actual, double tolerance)
    {
        Assert.True(
            Math.Abs(expected.Left - actual.Left) <= tolerance && Math.Abs(expected.Top - actual.Top) <= tolerance
            && Math.Abs(expected.Right - actual.Right) <= tolerance && Math.Abs(expected.Bottom - actual.Bottom) <= tolerance,
            $"Expected {expected}, got {actual} (tolerance {tolerance}).");
    }

    public static void AssertQuadsEqual(IReadOnlyList<Quad> expected, IReadOnlyList<Quad> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            AssertPointNear(expected[i].TopLeft, actual[i].TopLeft);
            AssertPointNear(expected[i].TopRight, actual[i].TopRight);
            AssertPointNear(expected[i].BottomLeft, actual[i].BottomLeft);
            AssertPointNear(expected[i].BottomRight, actual[i].BottomRight);
        }
    }

    private static void AssertPointNear(PagePoint expected, PagePoint actual) =>
        Assert.True(Math.Abs(expected.X - actual.X) <= 0.01 && Math.Abs(expected.Y - actual.Y) <= 0.01, $"Expected {expected}, got {actual}.");
}

/// <summary>A folder under the system temp folder, deleted on dispose.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder() =>
        Path = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pagewing-annotations-" + Guid.NewGuid().ToString("N"))).FullName;

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A document may still hold the file for a moment; the system cleans the temp folder later.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>A clock that always reports the same time.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

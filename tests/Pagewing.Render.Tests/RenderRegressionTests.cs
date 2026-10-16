using System.Globalization;
using System.Text;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Render.Tests;

/// <summary>
/// Renders every PDF of <see cref="RenderCorpus"/> through <c>pagewing render</c> and compares each page with its
/// reference image in <c>testdata/references/&lt;stem&gt;/</c>, in the <see cref="CompareMode"/> of its case. With <c>PAGEWING_UPDATE_REFERENCES=1</c> the
/// renderings become the new references and the test fails on purpose, so an update is always reviewed. The update
/// mode runs only on Windows, where the shared reference set is generated; elsewhere it fails without writing.
/// </summary>
public sealed class RenderRegressionTests : IDisposable
{
    private const string UpdateVariable = "PAGEWING_UPDATE_REFERENCES";

    private readonly DirectoryInfo workDirectory = Directory.CreateTempSubdirectory("pagewing-render-tests-");

    public static TheoryData<string> Corpus => [.. RenderCorpus.Cases.Select(c => c.PdfName)];

    public void Dispose() => workDirectory.Delete(recursive: true);

    [Theory]
    [MemberData(nameof(Corpus))]
    public async Task PagesMatchTheReferenceImages(string pdfName)
    {
        var ct = TestContext.Current.CancellationToken;
        var renderCase = RenderCorpus.Get(pdfName);
        var rendered = await RenderCorpus.RenderAsync(renderCase, workDirectory.FullName, ct);
        var referenceDirectory = TestData.ReferenceDir(renderCase.Stem);
        var diffDirectory = Path.Combine(AppContext.BaseDirectory, "render-diff", renderCase.Stem);
        if (Directory.Exists(diffDirectory))
        {
            Directory.Delete(diffDirectory, recursive: true);
        }

        var renderedNames = rendered.Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        var stale = Directory.Exists(referenceDirectory)
            ? Directory.GetFiles(referenceDirectory, "*.png").Where(path => !renderedNames.Contains(Path.GetFileName(path))).Order(StringComparer.Ordinal).ToList()
            : [];

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            // The one shared reference set is generated on Windows; Linux renders must never be mixed into it.
            if (!OperatingSystem.IsWindows())
            {
                Assert.Fail($"{UpdateVariable}=1 is only supported on Windows: the shared reference set is generated there.");
            }

            var updated = await UpdateReferencesAsync(rendered, referenceDirectory, stale, renderCase.Mode, ct);
            Assert.Fail($"{updated} references updated — review the differences and commit them ({referenceDirectory}).");
        }

        var failures = new StringBuilder();
        foreach (var path in stale)
        {
            failures.AppendLine(CultureInfo.InvariantCulture, $"Reference without a rendered page: {path} (delete it or run with {UpdateVariable}=1).");
        }

        foreach (var file in rendered)
        {
            var name = Path.GetFileName(file);
            var referencePath = Path.Combine(referenceDirectory, name);
            if (!File.Exists(referencePath))
            {
                failures.AppendLine(CultureInfo.InvariantCulture, $"Reference image missing: {referencePath}. Run the tests with {UpdateVariable}=1 to create it, then review and commit it.");
                continue;
            }

            var actualPng = await File.ReadAllBytesAsync(file, ct);
            var expected = Rgb.Decode(await File.ReadAllBytesAsync(referencePath, ct));
            var actual = Rgb.Decode(actualPng);
            var result = ImageComparer.Compare(expected, actual, renderCase.Mode);
            if (result.Passed)
            {
                continue;
            }

            Directory.CreateDirectory(diffDirectory);
            var baseName = Path.GetFileNameWithoutExtension(name);
            var actualPath = Path.Combine(diffDirectory, $"{baseName}.actual.png");
            var diffPath = Path.Combine(diffDirectory, $"{baseName}.diff.png");
            await File.WriteAllBytesAsync(actualPath, actualPng, ct);
            await File.WriteAllBytesAsync(diffPath, ImageComparer.CreateDiffPng(expected, actual, renderCase.Mode), ct);
            failures.AppendLine(CultureInfo.InvariantCulture, $"{name}: {result.Summary}");
            failures.AppendLine(CultureInfo.InvariantCulture, $"  reference: {referencePath}");
            failures.AppendLine(CultureInfo.InvariantCulture, $"  actual:    {actualPath}");
            failures.AppendLine(CultureInfo.InvariantCulture, $"  diff:      {diffPath}");
        }

        Assert.True(failures.Length == 0, $"{pdfName} does not match its reference images:\n{failures}");
    }

    [Fact]
    public void EveryTestPdfIsInTheCorpusOrExplicitlySkipped()
    {
        var present = Directory.GetFiles(Path.Combine(TestData.Root, "testdata", "pdfs"), "*.pdf")
            .Select(path => Path.GetFileName(path))
            .Order(StringComparer.Ordinal)
            .ToList();
        var covered = RenderCorpus.Cases.Select(c => c.PdfName).Concat(RenderCorpus.Skipped).ToList();

        Assert.Equal(covered.Count, covered.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(present, covered.Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public async Task StrictComparisonDetectsAOnePixelShift()
    {
        var page = await RenderPageOneAsync("vector-shapes.pdf", CompareMode.Strict);

        AssertDetected(page, ShiftRight(page, 1), CompareMode.Strict);
    }

    [Fact]
    public async Task TolerantComparisonDetectsMissingText()
    {
        var page = await RenderPageOneAsync("text-basic.pdf", CompareMode.Tolerant);
        var (top, bottom) = FirstTextLine(page);
        var blanked = (byte[])page.Pixels.Clone();
        Array.Fill(blanked, (byte)0xFF, top * page.Width * 3, (bottom - top) * page.Width * 3);

        AssertDetected(page, page with { Pixels = blanked }, CompareMode.Tolerant);
    }

    [Fact]
    public async Task TolerantComparisonDetectsAnEightPixelShift()
    {
        var page = await RenderPageOneAsync("text-basic.pdf", CompareMode.Tolerant);

        AssertDetected(page, ShiftRight(page, 8), CompareMode.Tolerant);
    }

    [Fact]
    public async Task TolerantComparisonDetectsAColourChange()
    {
        // A 40 x 40 patch inside the filled Square annotation, at the worst block alignment (offset 4 to the 8 x 8 grid).
        const int Left = 140, Top = 260, Size = 40;
        var page = await RenderPageOneAsync("annotations.pdf", CompareMode.Tolerant);
        var changed = (byte[])page.Pixels.Clone();
        var fill = page.Pixel(Left, Top);
        for (var y = Top; y < Top + Size; y++)
        {
            for (var x = Left; x < Left + Size; x++)
            {
                Assert.Equal(fill, page.Pixel(x, y)); // the patch lies in a flat filled area
                changed[(((y * page.Width) + x) * 3) + 1] = (byte)Math.Max(0, fill.G - 60);
            }
        }

        AssertDetected(page, page with { Pixels = changed }, CompareMode.Tolerant);
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public async Task RenderingTwiceIsBitIdentical(string pdfName)
    {
        var ct = TestContext.Current.CancellationToken;
        var renderCase = RenderCorpus.Get(pdfName);

        var first = await RenderCorpus.RenderAsync(renderCase, Path.Combine(workDirectory.FullName, "first"), ct);
        var second = await RenderCorpus.RenderAsync(renderCase, Path.Combine(workDirectory.FullName, "second"), ct);

        Assert.Equal(first.Select(Path.GetFileName), second.Select(Path.GetFileName));
        for (var i = 0; i < first.Count; i++)
        {
            var a = await File.ReadAllBytesAsync(first[i], ct);
            var b = await File.ReadAllBytesAsync(second[i], ct);
            Assert.True(a.AsSpan().SequenceEqual(b), $"{Path.GetFileName(first[i])} differs between two renderings.");
        }
    }

    private static void AssertDetected(Rgb page, Rgb mutated, CompareMode mode)
    {
        var report = ImageComparer.Compare(page, mutated, mode);
        TestContext.Current.TestOutputHelper?.WriteLine(report.Summary);
        Assert.False(report.Passed, $"The mutation was not detected: {report.Summary}");
    }

    /// <summary>Moves the image <paramref name="dx"/> pixels to the right; the uncovered columns are white.</summary>
    private static Rgb ShiftRight(Rgb image, int dx)
    {
        var shifted = new byte[image.Pixels.Length];
        Array.Fill(shifted, (byte)0xFF);
        for (var y = 0; y < image.Height; y++)
        {
            var row = y * image.Width * 3;
            Array.Copy(image.Pixels, row, shifted, row + (dx * 3), (image.Width - dx) * 3);
        }

        return image with { Pixels = shifted };
    }

    /// <summary>The first band of rows that contains non-white pixels: [top, bottom).</summary>
    private static (int Top, int Bottom) FirstTextLine(Rgb page)
    {
        var top = -1;
        for (var y = 0; y < page.Height; y++)
        {
            var inked = page.Pixels.AsSpan(y * page.Width * 3, page.Width * 3).ContainsAnyExcept((byte)0xFF);
            if (inked && top < 0)
            {
                top = y;
            }
            else if (!inked && top >= 0)
            {
                return (top, y);
            }
        }

        throw new InvalidOperationException("The page has no text line.");
    }

    /// <summary>
    /// Writes the renderings that are missing as references or deviate beyond the tolerance and deletes stale
    /// references; renderings within the tolerance keep their reference, so a run on another OS does not churn them.
    /// </summary>
    private static async Task<int> UpdateReferencesAsync(IReadOnlyList<string> rendered, string referenceDirectory, IReadOnlyList<string> stale, CompareMode mode, CancellationToken ct)
    {
        var updated = 0;
        Directory.CreateDirectory(referenceDirectory);
        foreach (var file in rendered)
        {
            var referencePath = Path.Combine(referenceDirectory, Path.GetFileName(file));
            var png = await File.ReadAllBytesAsync(file, ct);
            if (File.Exists(referencePath)
                && ImageComparer.Compare(Rgb.Decode(await File.ReadAllBytesAsync(referencePath, ct)), Rgb.Decode(png), mode).Passed)
            {
                continue;
            }

            await File.WriteAllBytesAsync(referencePath, png, ct);
            updated++;
        }

        foreach (var path in stale)
        {
            File.Delete(path);
            updated++;
        }

        return updated;
    }

    /// <summary>Renders page 1 of a corpus PDF and checks that the corpus compares it in <paramref name="mode"/>.</summary>
    private async Task<Rgb> RenderPageOneAsync(string pdfName, CompareMode mode)
    {
        var ct = TestContext.Current.CancellationToken;
        var renderCase = RenderCorpus.Get(pdfName);
        Assert.Equal(mode, renderCase.Mode);
        var files = await RenderCorpus.RenderAsync(renderCase, Path.Combine(workDirectory.FullName, "mutation"), ct, pages: "1");
        return Rgb.Decode(await File.ReadAllBytesAsync(Assert.Single(files), ct));
    }
}

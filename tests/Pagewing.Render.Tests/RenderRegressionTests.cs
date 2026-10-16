using System.Globalization;
using System.Text;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Render.Tests;

/// <summary>
/// Renders every PDF of <see cref="RenderCorpus"/> through <c>pagewing render</c> and compares each page with its
/// reference image in <c>testdata/references/&lt;stem&gt;/</c>. With <c>PAGEWING_UPDATE_REFERENCES=1</c> the
/// renderings become the new references and the test fails on purpose, so an update is always reviewed.
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
            var updated = await UpdateReferencesAsync(rendered, referenceDirectory, stale, ct);
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
            var result = ImageComparer.Compare(expected, actual, RenderTolerance.Default);
            if (result.Passed)
            {
                continue;
            }

            Directory.CreateDirectory(diffDirectory);
            var baseName = Path.GetFileNameWithoutExtension(name);
            var actualPath = Path.Combine(diffDirectory, $"{baseName}.actual.png");
            var diffPath = Path.Combine(diffDirectory, $"{baseName}.diff.png");
            await File.WriteAllBytesAsync(actualPath, actualPng, ct);
            await File.WriteAllBytesAsync(diffPath, ImageComparer.CreateDiffPng(expected, actual, RenderTolerance.Default), ct);
            failures.AppendLine(CultureInfo.InvariantCulture, $"{name}: {Describe(expected, actual, result)}");
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
    public async Task ComparerDetectsAOnePixelShift()
    {
        var page = await RenderTextBasicPageOneAsync();
        var shifted = new byte[page.Pixels.Length];
        Array.Fill(shifted, (byte)0xFF);
        for (var y = 0; y < page.Height; y++)
        {
            var row = y * page.Width * 3;
            Array.Copy(page.Pixels, row, shifted, row + 3, (page.Width - 1) * 3);
        }

        var result = ImageComparer.Compare(page, page with { Pixels = shifted }, RenderTolerance.Default);

        TestContext.Current.TestOutputHelper?.WriteLine(Describe(page, page, result));
        Assert.False(result.Passed, Describe(page, page, result));
    }

    [Fact]
    public async Task ComparerDetectsMissingText()
    {
        var page = await RenderTextBasicPageOneAsync();
        var (top, bottom) = FirstTextLine(page);
        var blanked = (byte[])page.Pixels.Clone();
        Array.Fill(blanked, (byte)0xFF, top * page.Width * 3, (bottom - top) * page.Width * 3);

        var result = ImageComparer.Compare(page, page with { Pixels = blanked }, RenderTolerance.Default);

        TestContext.Current.TestOutputHelper?.WriteLine($"rows {top}..{bottom - 1}: {Describe(page, page, result)}");
        Assert.False(result.Passed, Describe(page, page, result));
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

    private static string Describe(Rgb expected, Rgb actual, ComparisonResult result) =>
        result.SizeMatches
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{result.DifferingPixels} of {(long)expected.Width * expected.Height} pixels differ (fraction {result.Fraction:0.######}, allowed {RenderTolerance.Default.MaxDifferingFraction}), max channel delta {result.MaxDelta} (allowed {RenderTolerance.Default.MaxChannelDelta}).")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"size {actual.Width} x {actual.Height} differs from the reference {expected.Width} x {expected.Height}.");

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
    private static async Task<int> UpdateReferencesAsync(IReadOnlyList<string> rendered, string referenceDirectory, IReadOnlyList<string> stale, CancellationToken ct)
    {
        var updated = 0;
        Directory.CreateDirectory(referenceDirectory);
        foreach (var file in rendered)
        {
            var referencePath = Path.Combine(referenceDirectory, Path.GetFileName(file));
            var png = await File.ReadAllBytesAsync(file, ct);
            if (File.Exists(referencePath)
                && ImageComparer.Compare(Rgb.Decode(await File.ReadAllBytesAsync(referencePath, ct)), Rgb.Decode(png), RenderTolerance.Default).Passed)
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

    private async Task<Rgb> RenderTextBasicPageOneAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var files = await RenderCorpus.RenderAsync(RenderCorpus.Get("text-basic.pdf"), workDirectory.FullName, ct, pages: "1");
        return Rgb.Decode(await File.ReadAllBytesAsync(Assert.Single(files), ct));
    }
}

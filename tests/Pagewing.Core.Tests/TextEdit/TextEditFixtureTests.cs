using Pagewing.Core.Pdf;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.TextEdit;

/// <summary>The text edit fixtures (<see cref="TextEditFixtures"/>) are what they say they are: PDFium reads each known line where the generator wrote it.</summary>
public sealed class TextEditFixtureTests
{
    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Files() =>
        ["text-edit-subset.pdf", "text-edit-standard.pdf", "text-edit-full.pdf", "text-edit-styles.pdf", "text-edit-rotated.pdf", "text-edit-locked.pdf"];

    [Theory]
    [MemberData(nameof(Files))]
    public async Task EveryKnownLineIsInThePageTextAtItsOrigin(string file)
    {
        await using var document = await engine.OpenAsync(TestData.Pdf(file), cancellationToken: Ct);
        foreach (var run in TextEditFixtures.KnownRuns.Where(r => r.File == file && r.Text is not ("Vertical" or "ABBA" or "Shown text")))
        {
            var text = await document.GetPageTextAsync(run.Page, Ct);
            var all = text.GetText(0, text.Chars.Count);
            var at = all.IndexOf(run.Text.TrimEnd(), StringComparison.Ordinal);
            Assert.True(at >= 0, $"{file} page {run.Page + 1}: \"{run.Text}\" is not in \"{all}\"");
            var box = text.Chars[at].Box.Inflate(run.Size * 0.6);
            Assert.True(box.Contains(run.Left, run.Baseline), $"{file}: \"{run.Text}\" starts at {text.Chars[at].Box}, expected the origin {run.Left},{run.Baseline}");
        }
    }

    [Fact]
    public async Task ActualTextReplacesTheExtractedTextOfItsContent()
    {
        // PDFium extracts the /ActualText instead of the shown text: a search finds "Replacement", so the run is locked (Spec 3.2).
        await using var document = await engine.OpenAsync(TestData.Pdf("text-edit-locked.pdf"), cancellationToken: Ct);
        var text = await document.GetPageTextAsync(0, Ct);
        var all = text.GetText(0, text.Chars.Count);

        Assert.Contains("Replacement", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Shown text", all, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostileFixtureHasTheAnnouncedShape()
    {
        await using var document = await engine.OpenAsync(TestData.Pdf("text-edit-hostile.pdf"), cancellationToken: Ct);

        Assert.Equal(2, document.PageCount);
        var grid = await document.GetPageTextAsync(0, Ct);
        Assert.Equal(TextEditFixtures.HostileRunCount, grid.Chars.Count(static c => c.CodePoint == 'x'));
        var line = await document.GetPageTextAsync(1, Ct);
        Assert.Equal(TextEditFixtures.HostileRunLength, line.Chars.Count(static c => c.CodePoint == 'a'));
    }

    [Fact]
    public async Task SubsetFontsReallyLackTheOtherGlyphs()
    {
        // The embedded programs are subsets: they are far smaller than the complete font (the full file embeds all of Geist).
        var subset = new FileInfo(TestData.Pdf("text-edit-subset.pdf")).Length;
        var full = new FileInfo(TestData.Pdf("text-edit-full.pdf")).Length;

        Assert.True(subset * 4 < full, $"subset file {subset} bytes, full file {full} bytes");
        await using var document = await engine.OpenAsync(TestData.Pdf("text-edit-subset.pdf"), cancellationToken: Ct);
        Assert.Equal(1, document.PageCount);
    }
}

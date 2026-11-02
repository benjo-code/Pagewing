using Pagewing.Core.Ocr;
using Pagewing.Core.Pdf;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Ocr;

/// <summary>The run with the real engine on the scan fixtures (these skip without the native library, see <see cref="OcrTestLibrary"/>).</summary>
public sealed class OcrRunnerNativeTests
{
    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("ocr-scan.pdf")]
    [InlineData("ocr-scan-rotated.pdf")]
    public async Task ScanFixtureIsRecognisedAtTheKnownPositions(string file)
    {
        OcrTestLibrary.Require();
        await using var document = await engine.OpenAsync(TestData.Pdf(file), cancellationToken: Ct);
        var factory = new TesseractEngineFactory(OcrTestLibrary.Tessdata);

        var result = await OcrRunner.RunAsync(document, new OcrOptions("deu+eng", OcrMode.Skip, 0, null, 2), factory, null, Ct);

        Assert.Empty(result.Report.Skipped);
        var expected = 0;
        var found = new List<string>();
        foreach (var page in result.Pages)
        {
            foreach (var word in OcrFixtures.WordsOf(file, page.Page))
            {
                expected++;
                var text = word.Text.Trim(',', '.', ';');
                if (page.Words.Any(w => w.Text.Trim(',', '.', ';') == text
                    && Math.Abs(w.Left - word.Left) < 5 && Math.Abs(w.Right - word.Right) < 5
                    && Math.Abs(w.Top - word.Top) < 6 && Math.Abs(w.Bottom - word.Bottom) < 6))
                {
                    found.Add(word.Text);
                }
            }
        }

        // Clean synthetic text: nearly every word stands where the fixture drew it. The report counts them and never calls it complete.
        Assert.True(found.Count >= expected - 2, $"{found.Count} of {expected} words found at their place");
        Assert.True(result.Report.MeanConfidence > 70, $"mean confidence {result.Report.MeanConfidence:0.#}");
        Assert.Equal(result.Pages.Sum(static p => p.Words.Count), result.Report.Words);
    }
}

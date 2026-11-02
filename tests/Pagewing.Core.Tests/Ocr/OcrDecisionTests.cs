using Pagewing.Core.Ocr;
using Pagewing.Ocr;
using Xunit;

namespace Pagewing.Core.Tests.Ocr;

/// <summary>The decisions of an OCR run that need neither PDFium nor the engine: which pages, at what resolution, where the words stand, what the report says.</summary>
public sealed class OcrDecisionTests
{
    private static PageOcrInfo Info(int characters, bool ownLayer = false, double? imageDpi = 200, double coverage = 1) => new(characters, ownLayer, imageDpi, coverage);

    [Fact]
    public void FortyNineCharactersRunWithALittleTextNote()
    {
        var decision = OcrPlanner.Decide(Info(49), OcrMode.Skip);

        Assert.True(decision.Run);
        Assert.Null(decision.Reason);
        Assert.True(decision.LittleText);
    }

    [Fact]
    public void FiftyCharactersSkipInSkipModeWithHasText()
    {
        var decision = OcrPlanner.Decide(Info(50), OcrMode.Skip);

        Assert.False(decision.Run);
        Assert.Equal(OcrSkipReason.HasText, decision.Reason);
        Assert.False(decision.LittleText);
    }

    [Fact]
    public void ANoTextPageRunsWithoutANote()
    {
        var decision = OcrPlanner.Decide(Info(0), OcrMode.Skip);

        Assert.True(decision.Run);
        Assert.False(decision.LittleText);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(49)]
    [InlineData(50)]
    [InlineData(5000)]
    public void ForceRunsPagesWithText(int characters)
    {
        Assert.True(OcrPlanner.Decide(Info(characters), OcrMode.Force).Run);
    }

    [Fact]
    public void OwnLayerCountsAsAlreadyRecognizedUnlessRedo()
    {
        var skip = OcrPlanner.Decide(Info(0, ownLayer: true), OcrMode.Skip);
        var redo = OcrPlanner.Decide(Info(0, ownLayer: true), OcrMode.Redo);

        Assert.False(skip.Run);
        Assert.Equal(OcrSkipReason.AlreadyRecognized, skip.Reason);
        Assert.True(redo.Run);
    }

    [Fact]
    public void RedoStillSkipsAPageWithRealText()
    {
        var decision = OcrPlanner.Decide(Info(120), OcrMode.Redo);

        Assert.False(decision.Run);
        Assert.Equal(OcrSkipReason.HasText, decision.Reason);
    }

    [Theory]
    [InlineData(600, 300)]
    [InlineData(300, 300)]
    [InlineData(220, 220)]
    [InlineData(150, 150)]
    [InlineData(100, 150)]
    public void AutoUsesImageDpiClampedTo150Through300(double imageDpi, int expected)
    {
        var raster = OcrResolution.Resolve(360, 504, imageDpi, 0);

        Assert.Equal(expected, raster.Dpi);
        Assert.False(raster.Lowered);
        Assert.False(raster.TooLow);
    }

    [Fact]
    public void AutoWithoutAnImageUses300()
    {
        var raster = OcrResolution.Resolve(595, 842, null, 0);

        Assert.Equal(300, raster.Dpi);
        Assert.Equal(2480, raster.WidthPx);
        Assert.Equal(3509, raster.HeightPx); // 842 pt at 300 dpi is 3508.3 pixels, rounded up like the renderer does
    }

    [Fact]
    public void RequestedDpiIsRespected()
    {
        foreach (var dpi in new[] { 150, 200, 300, 400 })
        {
            Assert.Equal(dpi, OcrResolution.Resolve(360, 504, 100, dpi).Dpi);
        }
    }

    [Fact]
    public void OverTheLimitLowersAndReports()
    {
        // A4 at 600 dpi is 35 megapixels; a poster of 40 x 30 inches at 300 dpi is 108 megapixels.
        var raster = OcrResolution.Resolve(2880, 2160, 300, 0);

        Assert.True(raster.Lowered);
        Assert.True((long)raster.WidthPx * raster.HeightPx <= OcrResolution.MaxPixels);
        Assert.InRange(raster.Dpi, 150, 299);
        Assert.False(raster.TooLow);
    }

    [Fact]
    public void BelowTheMinimumIsTooLow()
    {
        var raster = OcrResolution.Resolve(14400, 14400, null, 0);

        Assert.True(raster.Lowered);
        Assert.True(raster.TooLow);
        Assert.True(raster.Dpi < OcrResolution.MinDpi);
        Assert.True((long)raster.WidthPx * raster.HeightPx <= OcrResolution.MaxPixels);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-5, 100)]
    [InlineData(double.NaN, 100)]
    [InlineData(100, double.PositiveInfinity)]
    public void ANonsensePageSizeIsRefused(double width, double height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OcrResolution.Resolve(width, height, 300, 0));
    }

    [Fact]
    public void WordsMapToPointsOfTheDisplayedPage()
    {
        // 1000 x 1400 pixels are the page of 360 x 504 points (200 dpi): a pixel is 0.36 points.
        var raster = new OcrRaster(200, 1000, 1400, false, false);
        var word = new OcrWord("Pagewing", 250, 100, 750, 200, 91.5f);

        var mapped = OcrWordMapper.ToPagePoints(word, raster, 360, 504);

        Assert.Equal("Pagewing", mapped.Text);
        Assert.Equal(90, mapped.Left, 0.01);
        Assert.Equal(36, mapped.Top, 0.01);
        Assert.Equal(270, mapped.Right, 0.01);
        Assert.Equal(72, mapped.Bottom, 0.01);
        Assert.Equal(91.5f, mapped.Confidence);
    }

    [Theory]
    [InlineData(360, 504)]
    [InlineData(504, 360)]
    public void RotatedAndCropBoxPagesMapToUprightPoints(double displayedWidth, double displayedHeight)
    {
        // The page is rendered as displayed (turned and cropped by PDFium), so the pixels are already upright: only the scale is left, and it is
        // the same for every rotation, whatever the media box says.
        var raster = OcrResolution.Resolve(displayedWidth, displayedHeight, 200, 0);
        var word = new OcrWord("x", 0, 0, raster.WidthPx, raster.HeightPx, 90);

        var mapped = OcrWordMapper.ToPagePoints(word, raster, displayedWidth, displayedHeight);

        Assert.Equal(0, mapped.Left, 0.001);
        Assert.Equal(0, mapped.Top, 0.001);
        Assert.Equal(displayedWidth, mapped.Right, 0.001);
        Assert.Equal(displayedHeight, mapped.Bottom, 0.001);
    }

    [Fact]
    public void AWordBeyondThePageIsCutToIt()
    {
        var raster = new OcrRaster(200, 1000, 1400, false, false);

        var mapped = OcrWordMapper.ToPagePoints(new OcrWord("edge", -20, -5, 1040, 1500, 80), raster, 360, 504);

        Assert.Equal(0, mapped.Left);
        Assert.Equal(0, mapped.Top);
        Assert.Equal(360, mapped.Right);
        Assert.Equal(504, mapped.Bottom);
        Assert.True(mapped.IsUsable);
    }

    [Fact]
    public void AnEmptyBoxIsNotUsable()
    {
        var raster = new OcrRaster(200, 1000, 1400, false, false);

        Assert.False(OcrWordMapper.ToPagePoints(new OcrWord("dot", 500, 500, 500, 520, 80), raster, 360, 504).IsUsable);
        Assert.False(OcrWordMapper.ToPagePoints(new OcrWord(" ", 500, 500, 540, 520, 80), raster, 360, 504).IsUsable);
    }

    [Fact]
    public void ReportMeanIsWeightedByWordsAndLowPagesAreListed()
    {
        var good = new OcrPageResult(0, Words(10, 90), 200, false, 90);
        var poor = new OcrPageResult(2, Words(30, 40), 200, false, 40);
        var blank = new OcrPageResult(3, [], 200, false, 0);

        var report = OcrReport.Build([good, poor, blank], [new OcrSkippedPage(1, OcrSkipReason.HasText)], littleText: [0]);

        Assert.Equal(3, report.PagesRecognized);
        Assert.Equal(40, report.Words);
        Assert.Equal(52.5, report.MeanConfidence, 0.01);
        Assert.Equal([2], report.LowConfidencePages);
        Assert.Equal([3], report.PagesWithoutWords);
        Assert.Equal([0], report.LittleTextPages);
        Assert.Equal(OcrSkipReason.HasText, Assert.Single(report.Skipped).Reason);
    }

    [Fact]
    public void ReportOfNothingRecognizedHasNoMeanAndNoPages()
    {
        var report = OcrReport.Build([], [new OcrSkippedPage(0, OcrSkipReason.TooLarge)], []);

        Assert.Equal(0, report.PagesRecognized);
        Assert.Equal(0, report.Words);
        Assert.Equal(0, report.MeanConfidence);
        Assert.Empty(report.LowConfidencePages);
    }

    [Fact]
    public void ReportKeepsPageOrderWhateverTheInputOrder()
    {
        var report = OcrReport.Build(
            [new OcrPageResult(5, Words(1, 30), 200, false, 30), new OcrPageResult(1, Words(1, 30), 200, false, 30)],
            [new OcrSkippedPage(4, OcrSkipReason.Timeout), new OcrSkippedPage(2, OcrSkipReason.HasText)],
            []);

        Assert.Equal([1, 5], report.LowConfidencePages);
        Assert.Equal([2, 4], report.Skipped.Select(static s => s.Page));
    }

    [Theory]
    [InlineData("eng", "eng")]
    [InlineData("deu+eng", "deu+eng")]
    [InlineData(" deu + eng ", "deu+eng")]
    [InlineData("deu+deu+eng", "deu+eng")]
    [InlineData("", null)]
    [InlineData("ENG", null)]
    [InlineData("../eng", null)]
    [InlineData("eng+", null)]
    [InlineData(null, null)]
    public void LanguageSelectionsAreNormalized(string? input, string? expected)
    {
        Assert.Equal(expected, OcrLanguages.Normalize(input));
    }

    [Fact]
    public void ADefaultIsTheUiLanguagePlusEnglishAtMostTwo()
    {
        string[] installed = ["deu", "eng", "fra"];

        Assert.Equal("deu+eng", OcrLanguages.DefaultFor("de", installed));
        Assert.Equal("eng", OcrLanguages.DefaultFor("en", installed));
        Assert.Equal("fra+eng", OcrLanguages.DefaultFor("fr", installed));
        Assert.Equal("eng", OcrLanguages.DefaultFor("ja", installed));
        Assert.Equal("deu", OcrLanguages.DefaultFor("de", ["deu"]));
        Assert.Equal(string.Empty, OcrLanguages.DefaultFor("de", []));
    }

    [Fact]
    public void MoreThanThreeLanguagesWarn()
    {
        Assert.False(OcrLanguages.IsSlow("deu+eng+fra"));
        Assert.True(OcrLanguages.IsSlow("deu+eng+fra+spa"));
    }

    private static OcrPageWord[] Words(int count, float confidence) =>
        [.. Enumerable.Range(0, count).Select(i => new OcrPageWord("w" + i, i, 0, i + 5, 10, confidence))];
}

using System.Globalization;
using Pagewing.Core.Pdf;
using Xunit;

namespace Pagewing.Core.Tests.Pdf;

public sealed class PageFormatTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static PdfPageSize Mm(double width, double height) => new(width * 72 / 25.4, height * 72 / 25.4);

    [Fact]
    public void A4PortraitAndLandscape()
    {
        Assert.Equal("A4 · 210 × 297 mm", PageFormat.Describe(new PdfPageSize(595.28, 841.89), Invariant));
        Assert.Equal("A4 · 297 × 210 mm", PageFormat.Describe(new PdfPageSize(841.89, 595.28), Invariant));
    }

    [Fact]
    public void OtherIsoSizesAreNamed()
    {
        Assert.Equal("A3 · 297 × 420 mm", PageFormat.Describe(Mm(297, 420), Invariant));
        Assert.Equal("A5 · 148 × 210 mm", PageFormat.Describe(Mm(148, 210), Invariant));
        Assert.Equal("A6 · 105 × 148 mm", PageFormat.Describe(Mm(105, 148), Invariant));
    }

    [Fact]
    public void LetterAndLegal()
    {
        Assert.Equal("Letter · 216 × 279 mm", PageFormat.Describe(new PdfPageSize(612, 792), Invariant));
        Assert.Equal("Letter · 279 × 216 mm", PageFormat.Describe(new PdfPageSize(792, 612), Invariant));
        Assert.Equal("Legal · 216 × 356 mm", PageFormat.Describe(new PdfPageSize(612, 1008), Invariant));
    }

    [Fact]
    public void UnknownSizeIsGivenInMillimetres()
    {
        Assert.Equal("200 × 300 mm", PageFormat.Describe(Mm(200, 300), Invariant));
        Assert.Equal("71 × 35 mm", PageFormat.Describe(new PdfPageSize(200, 100), Invariant));
    }

    [Fact]
    public void ToleranceIsOnePointFiveMillimetres()
    {
        Assert.StartsWith("A4", PageFormat.Describe(Mm(211.4, 297), Invariant), StringComparison.Ordinal);
        Assert.StartsWith("A4", PageFormat.Describe(Mm(210, 295.6), Invariant), StringComparison.Ordinal);
        Assert.Equal("212 × 297 mm", PageFormat.Describe(Mm(211.6, 297), Invariant));
        Assert.Equal("210 × 299 mm", PageFormat.Describe(Mm(210, 298.6), Invariant));
    }

    [Fact]
    public void NumbersFollowTheCulture()
    {
        // A culture built from the invariant one needs no ICU data.
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NumberGroupSeparator = "'";

        Assert.Equal("1'000 × 2'000 mm", PageFormat.Describe(Mm(1000, 2000), culture));
        Assert.Equal("1,000 × 2,000 mm", PageFormat.Describe(Mm(1000, 2000), Invariant));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-5, 100)]
    [InlineData(double.NaN, 100)]
    [InlineData(100, double.PositiveInfinity)]
    public void InvalidSizeGivesAnEmptyString(double width, double height)
    {
        Assert.Equal(string.Empty, PageFormat.Describe(new PdfPageSize(width, height), Invariant));
    }
}

using Pagewing.Core.Pdf;
using Xunit;

namespace Pagewing.Core.Tests.Pdf;

public sealed class PdfDateTests
{
    [Fact]
    public void ParsesFullDateWithOffset()
    {
        var date = PdfDate.TryParse("D:20240131120530+01'00'");

        Assert.Equal(new DateTimeOffset(2024, 1, 31, 12, 5, 30, TimeSpan.FromHours(1)), date);
    }

    [Fact]
    public void ParsesNegativeOffsetWithMinutes()
    {
        var date = PdfDate.TryParse("D:20240131120530-03'30'");

        Assert.Equal(new DateTimeOffset(2024, 1, 31, 12, 5, 30, new TimeSpan(-3, -30, 0)), date);
    }

    [Fact]
    public void ParsesDateOnly()
    {
        var date = PdfDate.TryParse("D:20240131");

        Assert.NotNull(date);
        Assert.Equal(new DateTime(2024, 1, 31, 0, 0, 0), date.Value.DateTime);
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(new DateTime(2024, 1, 31)), date.Value.Offset);
    }

    [Fact]
    public void ParsesYearOnly()
    {
        var date = PdfDate.TryParse("D:2024");

        Assert.Equal(new DateTime(2024, 1, 1), date?.DateTime);
    }

    [Fact]
    public void ParsesUtcSuffix()
    {
        Assert.Equal(new DateTimeOffset(2024, 1, 31, 12, 0, 0, TimeSpan.Zero), PdfDate.TryParse("D:20240131120000Z"));
        Assert.Equal(new DateTimeOffset(2024, 1, 31, 12, 0, 0, TimeSpan.Zero), PdfDate.TryParse("D:20240131120000Z00'00'"));
    }

    [Fact]
    public void ParsesOffsetWithoutApostrophes()
    {
        Assert.Equal(
            new DateTimeOffset(2024, 1, 31, 12, 0, 0, TimeSpan.FromHours(2)),
            PdfDate.TryParse("D:20240131120000+0200"));
        Assert.Equal(
            new DateTimeOffset(2024, 1, 31, 12, 0, 0, TimeSpan.FromHours(2)),
            PdfDate.TryParse("D:20240131120000+02"));
    }

    [Fact]
    public void ParsesWithoutPrefixAndWithSurroundingWhitespace()
    {
        Assert.Equal(new DateTime(2024, 1, 31, 12, 0, 0), PdfDate.TryParse("  20240131120000 ")?.DateTime);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("D:")]
    [InlineData("D:abcd")]
    [InlineData("D:202")]
    [InlineData("D:20241")]
    [InlineData("D:20240131120000+")]
    [InlineData("D:20240131120000+xx'yy'")]
    [InlineData("D:20240131120000Zjunk")]
    [InlineData("D:20240131120000+99'00'")]
    [InlineData("D:0000")]
    public void GarbageGivesNull(string value)
    {
        Assert.Null(PdfDate.TryParse(value));
    }

    [Fact]
    public void NullGivesNull()
    {
        Assert.Null(PdfDate.TryParse(null));
    }

    [Theory]
    [InlineData("D:20241301")]
    [InlineData("D:20240001")]
    [InlineData("D:20240230")]
    [InlineData("D:20240132")]
    [InlineData("D:2024013124")]
    [InlineData("D:202401311260")]
    [InlineData("D:20240131120060")]
    public void MonthThirteenGivesNull(string value)
    {
        Assert.Null(PdfDate.TryParse(value));
    }
}

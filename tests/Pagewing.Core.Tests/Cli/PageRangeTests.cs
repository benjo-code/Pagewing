using Pagewing.Core.Pdf;
using Xunit;

namespace Pagewing.Core.Tests.Cli;

public sealed class PageRangeTests
{
    [Theory]
    [InlineData("3", 5, new[] { 2 })]
    [InlineData("2-4", 5, new[] { 1, 2, 3 })]
    [InlineData("1,3,5-6", 6, new[] { 0, 2, 4, 5 })]
    [InlineData("all", 3, new[] { 0, 1, 2 })]
    [InlineData("ALL", 2, new[] { 0, 1 })]
    [InlineData("2,2,1", 2, new[] { 0, 1 })]
    [InlineData("4-5,1-2,2-4", 5, new[] { 0, 1, 2, 3, 4 })]
    [InlineData(" 1 , 3 - 4 ", 4, new[] { 0, 2, 3 })]
    public void ValidRangesGiveAscendingZeroBasedIndexesWithoutDuplicates(string text, int pageCount, int[] expected)
    {
        Assert.Equal(expected, PageRange.Parse(text, pageCount));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("5-3")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("1-")]
    [InlineData("-3")]
    [InlineData("1,,2")]
    [InlineData("1,")]
    [InlineData("1-2-3")]
    [InlineData("+1")]
    [InlineData("1.5")]
    [InlineData("0-2")]
    public void InvalidSyntaxThrowsFormatException(string text)
    {
        Assert.Throws<FormatException>(() => PageRange.Parse(text, 10));
        Assert.Throws<FormatException>(() => PageRange.Validate(text));
    }

    [Theory]
    [InlineData("9", 2, 9)]
    [InlineData("1-3", 2, 3)]
    [InlineData("1,2,3", 2, 3)]
    [InlineData("99999999999", 2, int.MaxValue)]
    public void PagesBeyondTheDocumentThrowArgumentOutOfRange(string text, int pageCount, int offending)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => PageRange.Parse(text, pageCount));

        Assert.Equal(offending, ex.ActualValue);
    }

    [Fact]
    public void AllOfAnEmptyDocumentIsEmpty()
    {
        Assert.Empty(PageRange.Parse("all", 0));
    }

    [Fact]
    public void HugeRangeBeyondTheDocumentFailsWithoutAllocatingIt()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PageRange.Parse("1-2000000000", 1000));
    }
}

using Pagewing.Core.Updates;
using Xunit;

namespace Pagewing.Core.Tests.Updates;

public sealed class UpdateVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("0.1.0", 0, 1, 0)]
    [InlineData("v10.20.30", 10, 20, 30)]
    public void ParsesPlainAndPrefixed(string text, int major, int minor, int patch)
    {
        Assert.True(UpdateVersion.TryParse(text, out var version));
        Assert.Equal(new UpdateVersion(major, minor, patch), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.3-rc.1")]
    [InlineData("1.2.3+build5")]
    [InlineData("vv1.2.3")]
    [InlineData("V1.2.3")]
    [InlineData("1.2.x")]
    [InlineData("-1.2.3")]
    [InlineData("1.-2.3")]
    [InlineData("+1.2.3")]
    [InlineData(" 1.2.3")]
    [InlineData("1.2.3 ")]
    [InlineData("1..3")]
    [InlineData("99999999999.0.0")]
    [InlineData("garbage")]
    public void RejectsPrereleaseAndGarbage(string? text)
    {
        Assert.False(UpdateVersion.TryParse(text, out var version));
        Assert.Equal(default, version);
    }

    [Fact]
    public void ComparesNumericallyNotAsText()
    {
        var older = new UpdateVersion(0, 9, 0);
        var newer = new UpdateVersion(0, 10, 0);

        Assert.True(newer > older);
        Assert.True(older < newer);
        Assert.True(newer >= new UpdateVersion(0, 10, 0));
        Assert.True(older <= new UpdateVersion(0, 9, 0));
        Assert.False(older >= newer);
        Assert.False(newer <= older);
        Assert.True(newer.CompareTo(older) > 0);
        Assert.True(new UpdateVersion(1, 0, 0) > new UpdateVersion(0, 99, 99));
        Assert.True(new UpdateVersion(1, 2, 10) > new UpdateVersion(1, 2, 9));
        Assert.Equal(0, newer.CompareTo(new UpdateVersion(0, 10, 0)));
    }

    [Fact]
    public void FormatsAsDottedNumbers()
    {
        Assert.Equal("1.2.3", new UpdateVersion(1, 2, 3).ToString());
    }
}

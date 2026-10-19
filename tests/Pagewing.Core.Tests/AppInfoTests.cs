using Xunit;

namespace Pagewing.Core.Tests;

public sealed class AppInfoTests
{
    [Fact]
    public void VersionIsSemanticVersionWithoutBuildMetadata()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$", AppInfo.Version);
    }

    [Theory]
    [InlineData("1.2.3+abc", "1.2.3")]
    [InlineData("1.2.3-beta.1+abc.def", "1.2.3-beta.1")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("", "0.0.0")]
    [InlineData(null, "0.0.0")]
    public void VersionDropsTheBuildMetadata(string? informational, string expected)
    {
        Assert.Equal(expected, AppInfo.WithoutBuildMetadata(informational));
    }
}

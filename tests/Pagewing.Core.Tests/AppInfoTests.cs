using Xunit;

namespace Pagewing.Core.Tests;

public sealed class AppInfoTests
{
    [Fact]
    public void VersionIsSemanticVersionWithoutBuildMetadata()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$", AppInfo.Version);
    }
}

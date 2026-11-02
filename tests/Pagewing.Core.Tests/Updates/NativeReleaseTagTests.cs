using System.Text.RegularExpressions;
using Pagewing.Core.Updates;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Updates;

/// <summary>
/// The OCR library lives in a prerelease of this repository (<c>native-tesseract-&lt;version&gt;-&lt;n&gt;</c>, milestone 15). The update
/// check reads <c>releases/latest</c> and <c>release.yml</c> accepts SemVer tags only, so the tag must never look like a version.
/// The constants are read from <c>tools/package.cs</c> (<c>NativeTesseract</c>), the one place that pins them.
/// </summary>
public sealed class NativeReleaseTagTests
{
    private static string Tag()
    {
        var text = File.ReadAllText(Path.Combine(TestData.Root, "tools", "package.cs"));
        var version = Regex.Match(text, "TesseractVersion = \"(?<v>[^\"]+)\"", RegexOptions.None, TimeSpan.FromSeconds(5));
        var build = Regex.Match(text, @"BuildNumber = (?<n>\d+);", RegexOptions.None, TimeSpan.FromSeconds(5));
        Assert.True(version.Success && build.Success, "NativeTesseract.TesseractVersion / BuildNumber not found in tools/package.cs");
        return $"native-tesseract-{version.Groups["v"].Value}-{build.Groups["n"].Value}";
    }

    [Fact]
    public void TheTagIsNeverAVersionTheUpdateCheckCouldTake()
    {
        var tag = Tag();
        Assert.False(UpdateVersion.TryParse(tag, out _));
        Assert.False(UpdateVersion.TryParse(tag.TrimStart('v'), out _));
        Assert.StartsWith("native-tesseract-", tag, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTagDoesNotPassTheTagCheckOfTheReleaseWorkflow()
    {
        var workflow = File.ReadAllText(Path.Combine(TestData.Root, ".github", "workflows", "release.yml"));
        var rule = Regex.Match(workflow, @"\^v\[0-9\]\+[^'""\s]*\$", RegexOptions.None, TimeSpan.FromSeconds(5));
        Assert.True(rule.Success, "the SemVer tag rule was not found in release.yml");
        Assert.DoesNotMatch(rule.Value, Tag());
        Assert.Matches(rule.Value, "v1.2.3");
    }
}

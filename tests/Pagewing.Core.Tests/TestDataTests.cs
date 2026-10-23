using System.Text.RegularExpressions;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests;

public sealed partial class TestDataTests
{
    [Fact]
    public void AllListedPdfsExist()
    {
        var listed = File.ReadLines(TestData.Pdf("LICENSES.md"))
            .Select(line => FileRow().Match(line))
            .Where(match => match.Success)
            .Select(match => match.Groups["name"].Value)
            .ToList();
        Assert.NotEmpty(listed);

        foreach (var name in listed)
        {
            Assert.True(File.Exists(TestData.Pdf(name)), $"{name} is listed in LICENSES.md but missing in testdata/pdfs.");
        }

        var present = Directory.GetFiles(Path.GetDirectoryName(TestData.Pdf("LICENSES.md"))!, "*.pdf")
            .Select(path => Path.GetFileName(path))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(present, listed.Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>A table row of LICENSES.md whose first cell is a file name in backticks.</summary>
    [GeneratedRegex(@"^\|\s*`(?<name>[^`|]+\.pdf)`\s*\|")]
    private static partial Regex FileRow();
}

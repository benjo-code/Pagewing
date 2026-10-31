using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Pagewing.Core.Pdf;
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

    /// <summary>
    /// The signature files are structure dummies (no real cryptography); the DocMDP level sits in the
    /// <c>/TransformParams</c> of the signature reference, and the fourth file is signed without any DocMDP.
    /// </summary>
    [Theory]
    [InlineData("signed-docmdp-1.pdf", 1)]
    [InlineData("signed-docmdp-2.pdf", 2)]
    [InlineData("signed-docmdp-3.pdf", 3)]
    [InlineData("signed-docmdp-none.pdf", 0)]
    public void SignatureFixturesCarryTheExpectedDocMdpLevel(string name, int level)
    {
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(TestData.Pdf(name)));

        Assert.Contains("/Type /Sig", text, StringComparison.Ordinal);
        Assert.Contains("/ByteRange", text, StringComparison.Ordinal);
        if (level == 0)
        {
            Assert.DoesNotContain("/DocMDP", text, StringComparison.Ordinal);
            Assert.DoesNotContain("/TransformParams", text, StringComparison.Ordinal);
            return;
        }

        var match = DocMdpLevel().Match(text);
        Assert.True(match.Success, $"{name} has no DocMDP transform.");
        Assert.Equal(level, int.Parse(match.Groups["level"].Value, CultureInfo.InvariantCulture));
        Assert.Single(DocMdpLevel().Matches(text));
    }

    [Theory]
    [InlineData("signed-docmdp-1.pdf", 1)]
    [InlineData("signed-docmdp-2.pdf", 1)]
    [InlineData("signed-docmdp-3.pdf", 1)]
    [InlineData("signed-docmdp-none.pdf", 1)]
    [InlineData("linearized.pdf", 2)]
    [InlineData("annotations-foreign.pdf", 2)]
    [InlineData("drawings-foreign.pdf", 2)]
    public async Task PdfiumOpensTheStructureFixtures(string name, int pages)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await new PdfEngine().OpenAsync(TestData.Pdf(name), cancellationToken: ct);

        Assert.Equal(pages, document.PageCount);
    }

    [Fact]
    public void LinearizedFixtureStartsWithTheLinearizedDictionary()
    {
        var bytes = File.ReadAllBytes(TestData.Pdf("linearized.pdf"));
        var text = Encoding.Latin1.GetString(bytes);
        var firstObject = text.IndexOf(" obj", StringComparison.Ordinal);

        Assert.True(firstObject > 0);
        Assert.StartsWith("<< /Linearized 1", text[(firstObject + " obj".Length)..].TrimStart(), StringComparison.Ordinal);
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"/L {bytes.Length:D10}"), text, StringComparison.Ordinal);
    }

    /// <summary>The <c>/P</c> value inside <c>/TransformParams</c> of a signature reference.</summary>
    [GeneratedRegex(@"/TransformParams\s*<<[^>]*?/P\s+(?<level>\d+)\b")]
    private static partial Regex DocMdpLevel();

    /// <summary>A table row of LICENSES.md whose first cell is a file name in backticks.</summary>
    [GeneratedRegex(@"^\|\s*`(?<name>[^`|]+\.pdf)`\s*\|")]
    private static partial Regex FileRow();
}

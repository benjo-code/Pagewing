using System.Text;
using Pagewing.Cli;
using Pagewing.Cli.Commands;
using Pagewing.Core.Text;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Cli;

public sealed class SearchCliTests
{
    /// <summary>User password of the encrypted PDFium test files (see <c>testdata/pdfs/LICENSES.md</c>).</summary>
    private const string UserPassword = "hôtel";

    [Fact]
    public async Task SearchPrintsOneTabSeparatedLinePerHit()
    {
        var result = await RunAsync("search", TestData.Pdf("text-rich.pdf"), "Pagewing", "--match-case");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        var lines = result.OutputLines;
        Assert.Equal(3, lines.Length);
        Assert.Equal("1\t0\t[Pagewing] Handbuch", lines[0]);
        Assert.StartsWith("1\t", lines[1], StringComparison.Ordinal);
        Assert.EndsWith("\t[Pagewing] ist ein schneller PDF-Viewer ", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("2\t", lines[2], StringComparison.Ordinal);
        Assert.EndsWith("\tZiel: [Pagewing] am Ende.", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchWithoutHitsPrintsNothingAndExitsZero()
    {
        var result = await RunAsync("search", TestData.Pdf("text-rich.pdf"), "Zebrastreifen");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.Equal(string.Empty, result.Output);
    }

    [Fact]
    public async Task SearchHonoursPagesAndCaseOptions()
    {
        var all = await RunAsync("search", TestData.Pdf("text-rich.pdf"), "pagewing");
        var secondPage = await RunAsync("search", TestData.Pdf("text-rich.pdf"), "pagewing", "--pages", "2");
        var matchCase = await RunAsync("search", TestData.Pdf("text-rich.pdf"), "pagewing", "--match-case");

        Assert.Equal(0, all.ExitCode);
        Assert.Equal(4, all.OutputLines.Length);
        Assert.Equal(["1", "1", "1", "2"], all.OutputLines.Select(static line => line.Split('\t')[0]));
        Assert.Equal(0, secondPage.ExitCode);
        var single = Assert.Single(secondPage.OutputLines);
        Assert.StartsWith("2\t", single, StringComparison.Ordinal);
        Assert.Equal(0, matchCase.ExitCode);
        var lowercase = Assert.Single(matchCase.OutputLines);
        Assert.StartsWith("1\t", lowercase, StringComparison.Ordinal);
        Assert.Contains("https://example.org/[pagewing]", lowercase, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchWholeWordSkipsMatchesInsideWords()
    {
        var inside = await RunAsync("search", TestData.Pdf("text-rich.pdf"), "Pagewi");
        var whole = await RunAsync("search", TestData.Pdf("text-rich.pdf"), "Pagewi", "--whole-word");

        Assert.Equal(0, inside.ExitCode);
        Assert.NotEmpty(inside.OutputLines);
        Assert.Equal(0, whole.ExitCode);
        Assert.Equal(string.Empty, whole.Output);
    }

    [Fact]
    public async Task SearchPagesAreSearchedInAscendingOrderWhateverTheRangeSaysFirst()
    {
        var result = await RunAsync("search", TestData.Pdf("text-rich.pdf"), "Pagewing", "--match-case", "--pages", "2,1");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["1", "1", "2"], result.OutputLines.Select(static line => line.Split('\t')[0]));
    }

    [Fact]
    public async Task SearchWithoutAQueryIsAUsageError()
    {
        var result = await RunAsync("search", TestData.Pdf("text-rich.pdf"));

        AssertUsageError(result, "query");
    }

    [Fact]
    public async Task SearchWithoutFileAndQueryIsAUsageError()
    {
        AssertUsageError(await RunAsync("search"), "PDF file");
    }

    [Fact]
    public async Task SearchWithAThirdPositionalArgumentIsAUsageError()
    {
        var result = await RunAsync("search", TestData.Pdf("text-rich.pdf"), "Pagewing", "extra");

        AssertUsageError(result, "extra");
    }

    [Fact]
    public async Task SearchWithUnknownOptionsOrOptionValuesIsAUsageError()
    {
        AssertUsageError(await RunAsync("search", TestData.Pdf("text-rich.pdf"), "x", "--bogus"), "--bogus");
        AssertUsageError(await RunAsync("search", TestData.Pdf("text-rich.pdf"), "x", "--dpi", "72"), "--dpi");
        AssertUsageError(await RunAsync("search", TestData.Pdf("text-rich.pdf"), "x", "--match-case=1"), "--match-case");
        AssertUsageError(await RunAsync("search", TestData.Pdf("text-rich.pdf"), "x", "--whole-word=yes"), "--whole-word");
        AssertUsageError(await RunAsync("search", TestData.Pdf("text-rich.pdf"), "x", "--pages", "5-3"), "--pages");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task SearchWithAnEmptyOrBlankQueryIsAUsageError(string query)
    {
        var result = await RunAsync("search", TestData.Pdf("text-rich.pdf"), query);

        AssertUsageError(result, "The 'search' command needs a non-empty query.");
    }

    [Fact]
    public async Task SearchPageOutOfRangeExitsWithOne()
    {
        var result = await RunAsync("search", TestData.Pdf("text-rich.pdf"), "Pagewing", "--pages", "9");

        Assert.Equal(1, result.ExitCode);
        Assert.StartsWith("pagewing: Page 9 is out of range", result.Error, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Output);
    }

    [Fact]
    public async Task SearchDoubleDashEndsOptionParsingSoQueriesMayStartWithADash()
    {
        var withHit = await RunAsync("search", TestData.Pdf("text-rich.pdf"), "--", "-Viewer");
        var queryFirst = await RunAsync("search", "--", TestData.Pdf("text-rich.pdf"), "--match-case");

        Assert.Equal(0, withHit.ExitCode);
        Assert.Equal(string.Empty, withHit.Error);
        var line = Assert.Single(withHit.OutputLines);
        Assert.Contains("PDF[-Viewer]", line, StringComparison.Ordinal);
        Assert.Equal(0, queryFirst.ExitCode);
        Assert.Equal(string.Empty, queryFirst.Error);
        Assert.Equal(string.Empty, queryFirst.Output);
    }

    [Fact]
    public async Task SearchWithADashQueryOnTheCommandLineWithoutDoubleDashIsAnUnknownOption()
    {
        var result = await RunAsync("search", TestData.Pdf("text-rich.pdf"), "-x");

        AssertUsageError(result, "-x");
    }

    [Fact]
    public async Task SearchIsListedInHelpAndUsage()
    {
        var help = await RunAsync("--help");
        var usage = await RunAsync("search");

        Assert.Equal(0, help.ExitCode);
        Assert.Contains("pagewing search <file> <query> [--pages <range>] [--match-case] [--whole-word] [--password <pw>]", help.Output, StringComparison.Ordinal);
        Assert.Contains("  search ", help.Output, StringComparison.Ordinal);
        Assert.Contains("--match-case", help.Output, StringComparison.Ordinal);
        Assert.Contains("--whole-word", help.Output, StringComparison.Ordinal);
        Assert.Contains("<page>", help.Output, StringComparison.Ordinal);
        Assert.Contains("pagewing search <file> <query>", usage.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchInAPasswordFileNeedsThePassword()
    {
        var without = await RunAsync("search", TestData.Pdf("encrypted_hello_world_r3.pdf"), "world");
        var withEnvironment = await RunAsync(
            new Dictionary<string, string> { ["PAGEWING_PASSWORD"] = UserPassword },
            "search",
            TestData.Pdf("encrypted_hello_world_r3.pdf"),
            "world");
        var withOption = await RunAsync("search", TestData.Pdf("encrypted_hello_world_r3.pdf"), "world", "--password", UserPassword);

        Assert.Equal(1, without.ExitCode);
        Assert.Contains("Password required", without.Error, StringComparison.Ordinal);
        Assert.Equal(string.Empty, without.Output);
        foreach (var result in new[] { withEnvironment, withOption })
        {
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.Error);
            Assert.Equal(2, result.OutputLines.Length);
            Assert.Equal("1\t7\tHello, [world]!", result.OutputLines[0]);
        }
    }

    [Fact]
    public void FormatLineReplacesControlCharactersSoTheLineFormatHolds()
    {
        var hit = new SearchHit(
            PageIndex: 2,
            FirstChar: 41,
            LastChar: 45,
            Rects: [],
            Snippet: new SearchSnippet("a\tb\r\nc\u0085d", "x\u0000y\u001Fz", "\u007Fe\u2028f\u2029g"));

        var line = SearchCommand.FormatLine(hit);

        Assert.StartsWith("3\t41\t", line, StringComparison.Ordinal);
        Assert.Equal(2, line.Count(static c => c == '\t'));
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\n', line);
        Assert.Equal("a b  c d[x y z] e f g", line["3\t41\t".Length..]);
    }

    private static void AssertUsageError(CliResult result, string messagePart)
    {
        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        Assert.StartsWith("pagewing: ", result.Error, StringComparison.Ordinal);
        Assert.Contains(messagePart, result.Error, StringComparison.Ordinal);
        Assert.Contains("Usage:", result.Error, StringComparison.Ordinal);
    }

    private static Task<CliResult> RunAsync(params string[] args) => RunAsync(environment: null, args);

    private static async Task<CliResult> RunAsync(Dictionary<string, string>? environment, params string[] args)
    {
        using var stdout = new MemoryStream();
        using var stderr = new StringWriter();
        var exitCode = await CliApp.RunAsync(
            args,
            stdout,
            stderr,
            name => environment is not null && environment.TryGetValue(name, out var value) ? value : null,
            TestContext.Current.CancellationToken);
        return new CliResult(exitCode, stdout.ToArray(), stderr.ToString());
    }

    private sealed record CliResult(int ExitCode, byte[] OutputBytes, string Error)
    {
        public string Output { get; } = new UTF8Encoding(false, true).GetString(OutputBytes);

        public string[] OutputLines { get; } = new UTF8Encoding(false, true).GetString(OutputBytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }
}

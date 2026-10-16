using System.Text;
using Pagewing.Cli;
using Pagewing.Core.Pdf;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Cli;

public sealed class CliTests : IDisposable
{
    /// <summary>User password of the encrypted PDFium test files (see <c>testdata/pdfs/LICENSES.md</c>).</summary>
    private const string UserPassword = "hôtel";

    private readonly DirectoryInfo workDirectory = Directory.CreateTempSubdirectory("pagewing-cli-tests-");

    public void Dispose() => workDirectory.Delete(recursive: true);

    [Fact]
    public async Task InfoPrintsPagesVersionAndGroupedSizes()
    {
        var result = await RunAsync("info", TestData.Pdf("text-basic.pdf"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        var lines = result.OutputLines;
        Assert.Contains($"File: {TestData.Pdf("text-basic.pdf")}", lines);
        Assert.Contains("Pages: 2", lines);
        Assert.Contains("PDF version: 1.7", lines);
        Assert.Contains("Title: Pagewing Textprobe: Größe", lines);
        Assert.Contains("Producer: Pagewing test generator", lines);
        Assert.DoesNotContain(lines, line => line.StartsWith("Author:", StringComparison.Ordinal));
        Assert.Equal(["Page sizes:", "  1-2: 300.00 x 200.00 pt"], lines[Array.IndexOf(lines, "Page sizes:")..]);
    }

    [Fact]
    public async Task InfoGroupsOnlyConsecutivePagesOfEqualSize()
    {
        var single = await RunAsync("info", TestData.Pdf("wide-page.pdf"));
        var thousand = await RunAsync("info", TestData.Pdf("thousand-pages.pdf"));

        Assert.Equal(["Page sizes:", "  1: 600.00 x 120.00 pt"], single.OutputLines[Array.IndexOf(single.OutputLines, "Page sizes:")..]);
        Assert.Contains("Pages: 1000", thousand.OutputLines);
        Assert.Equal(["Page sizes:", "  1-1000: 200.00 x 100.00 pt"], thousand.OutputLines[Array.IndexOf(thousand.OutputLines, "Page sizes:")..]);
    }

    [Fact]
    public async Task RenderWritesNamedPngFilesWithExpectedSize()
    {
        var output = Path.Combine(workDirectory.FullName, "does-not-exist-yet", "png");
        Assert.False(Directory.Exists(output));

        var result = await RunAsync("render", TestData.Pdf("text-basic.pdf"), "--dpi", "144", "--output", output);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        var expectedFiles = new[] { Path.Combine(output, "text-basic-p001.png"), Path.Combine(output, "text-basic-p002.png") };
        Assert.Equal(expectedFiles, result.OutputLines);
        var expected = new PdfPageSize(300, 200).ToPixels(2);
        foreach (var file in expectedFiles)
        {
            var (width, height, _) = PngDecoder.Decode(await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken));
            Assert.Equal(expected, (width, height));
        }

        Assert.Equal(2, Directory.GetFiles(output).Length);
    }

    [Fact]
    public async Task RenderHonoursDpiAndNoAnnotations()
    {
        var ct = TestContext.Current.CancellationToken;
        var output = Path.Combine(workDirectory.FullName, "out");

        var withAnnotations = await RunAsync("render", TestData.Pdf("annotations.pdf"), "--dpi", "72", "--output", output);
        var annotated = PngDecoder.Decode(await File.ReadAllBytesAsync(Path.Combine(output, "annotations-p001.png"), ct));
        var withoutAnnotations = await RunAsync("render", TestData.Pdf("annotations.pdf"), "--dpi", "72", "--output", output, "--no-annotations");
        var plain = PngDecoder.Decode(await File.ReadAllBytesAsync(Path.Combine(output, "annotations-p001.png"), ct));

        Assert.Equal(0, withAnnotations.ExitCode);
        Assert.Equal(0, withoutAnnotations.ExitCode);
        Assert.Equal((300, 200), (annotated.Width, annotated.Height));
        Assert.Equal((300, 200), (plain.Width, plain.Height));
        Assert.NotEqual(annotated.Rgb, plain.Rgb);
    }

    [Fact]
    public async Task RenderPagesOptionRendersOnlyThosePages()
    {
        var output = Path.Combine(workDirectory.FullName, "pages");

        var result = await RunAsync("render", TestData.Pdf("text-basic.pdf"), "--pages", "2", "--output", output);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([Path.Combine(output, "text-basic-p002.png")], result.OutputLines);
        Assert.Equal([Path.Combine(output, "text-basic-p002.png")], Directory.GetFiles(output));
    }

    [Fact]
    public async Task RenderNamesFilesWithAsManyDigitsAsThePageCountNeeds()
    {
        var output = Path.Combine(workDirectory.FullName, "thousand");

        var result = await RunAsync("render", TestData.Pdf("thousand-pages.pdf"), "--pages", "7,1000", "--dpi", "36", "--output", output);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([Path.Combine(output, "thousand-pages-p0007.png"), Path.Combine(output, "thousand-pages-p1000.png")], result.OutputLines);
    }

    [Fact]
    public async Task RenderIntoAnExistingDirectoryWorks()
    {
        var result = await RunAsync("render", TestData.Pdf("hello_world.pdf"), "--dpi", "10", "--output", workDirectory.FullName);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([Path.Combine(workDirectory.FullName, "hello_world-p001.png")], result.OutputLines);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("")]
    public async Task InvalidDpiExitsWithTwo(string dpi)
    {
        var result = await RunAsync("render", TestData.Pdf("text-basic.pdf"), "--dpi", dpi, "--output", workDirectory.FullName);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--dpi", result.Error, StringComparison.Ordinal);
        Assert.Contains("Usage:", result.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(workDirectory.FullName));
    }

    [Theory]
    [InlineData("1000000")]
    [InlineData("1e300")]
    public async Task AbsurdDpiExitsWithOneAndCoreLimitMessage(string dpi)
    {
        var result = await RunAsync("render", TestData.Pdf("text-basic.pdf"), "--dpi", dpi, "--output", workDirectory.FullName);

        Assert.Equal(1, result.ExitCode);
        Assert.StartsWith("pagewing: ", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", result.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(workDirectory.FullName));
    }

    [Fact]
    public async Task TextPrintsExtractedTextWithFormFeeds()
    {
        var result = await RunAsync("text", TestData.Pdf("text-basic.pdf"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Hello, Pagewing\fGröße äöü\f", result.Output);
    }

    [Fact]
    public async Task TextPagesOptionSelectsPages()
    {
        var result = await RunAsync("text", TestData.Pdf("text-basic.pdf"), "--pages", "2");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Größe äöü\f", result.Output);
    }

    [Fact]
    public async Task EncryptedWithoutPasswordExitsWithOneAndHint()
    {
        var result = await RunAsync("info", TestData.Pdf("encrypted_hello_world_r3.pdf"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Password required", result.Error, StringComparison.Ordinal);
        Assert.Contains("--password", result.Error, StringComparison.Ordinal);
        Assert.Contains("PAGEWING_PASSWORD", result.Error, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Output);
    }

    [Theory]
    [InlineData("encrypted_hello_world_r3.pdf")]
    [InlineData("encrypted_hello_world_r6.pdf")]
    public async Task WrongPasswordExitsWithOne(string file)
    {
        var result = await RunAsync("text", TestData.Pdf(file), "--password", "1234");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("pagewing: The password is incorrect.", result.Error.TrimEnd());
    }

    [Theory]
    [InlineData("encrypted_hello_world_r3.pdf")]
    [InlineData("encrypted_hello_world_r6.pdf")]
    public async Task PasswordFromOptionAndFromEnvironmentWork(string file)
    {
        var fromOption = await RunAsync("text", TestData.Pdf(file), "--password", UserPassword);
        var fromEnvironment = await RunAsync(
            new Dictionary<string, string> { ["PAGEWING_PASSWORD"] = UserPassword },
            "text",
            TestData.Pdf(file));
        var optionWins = await RunAsync(
            new Dictionary<string, string> { ["PAGEWING_PASSWORD"] = "wrong" },
            "text",
            TestData.Pdf(file),
            $"--password={UserPassword}");

        foreach (var result in new[] { fromOption, fromEnvironment, optionWins })
        {
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("Hello, world!\nGoodbye, world!\f", result.Output);
        }
    }

    [Fact]
    public async Task MissingFileExitsWithOne()
    {
        var missing = Path.Combine(workDirectory.FullName, "missing.pdf");

        var result = await RunAsync("info", missing);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(missing, result.Error, StringComparison.Ordinal);
        Assert.StartsWith("pagewing: ", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotAPdfExitsWithOne()
    {
        var result = await RunAsync("text", TestData.Pdf("not-a-pdf.pdf"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("not-a-pdf.pdf", result.Error, StringComparison.Ordinal);
        Assert.Contains("not a PDF", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a\0b.pdf")]
    [InlineData("bad|name*.pdf")]
    public async Task UnusablePathsExitWithOneAndNoStackTrace(string file)
    {
        var result = await RunAsync("info", file);

        Assert.Equal(1, result.ExitCode);
        Assert.StartsWith("pagewing: ", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OverlongPathExitsWithOne()
    {
        var result = await RunAsync("info", Path.Combine(workDirectory.FullName, new string('x', 400), new string('y', 400) + ".pdf"));

        Assert.Equal(1, result.ExitCode);
        Assert.StartsWith("pagewing: ", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutputDirectoryThatIsAFileExitsWithOne()
    {
        var blocker = Path.Combine(workDirectory.FullName, "blocker");
        await File.WriteAllTextAsync(blocker, "x", TestContext.Current.CancellationToken);

        var result = await RunAsync("render", TestData.Pdf("text-basic.pdf"), "--output", blocker);

        Assert.Equal(1, result.ExitCode);
        Assert.StartsWith("pagewing: ", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownCommandAndMissingArgumentExitWithTwoAndUsage()
    {
        var unknown = await RunAsync("frobnicate", "x.pdf");
        var missingFile = await RunAsync("info");
        var extraFile = await RunAsync("info", "a.pdf", "b.pdf");
        var unknownOption = await RunAsync("info", TestData.Pdf("text-basic.pdf"), "--bogus");
        var optionOfAnotherCommand = await RunAsync("info", TestData.Pdf("text-basic.pdf"), "--pages", "1");
        var missingValue = await RunAsync("text", TestData.Pdf("text-basic.pdf"), "--pages");

        foreach (var result in new[] { unknown, missingFile, extraFile, unknownOption, optionOfAnotherCommand, missingValue })
        {
            Assert.Equal(2, result.ExitCode);
            Assert.Equal(string.Empty, result.Output);
            Assert.Contains("Usage:", result.Error, StringComparison.Ordinal);
            Assert.StartsWith("pagewing: ", result.Error, StringComparison.Ordinal);
        }

        Assert.Contains("frobnicate", unknown.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidPageRangeExitsWithTwo()
    {
        var result = await RunAsync("text", TestData.Pdf("text-basic.pdf"), "--pages", "5-3");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--pages", result.Error, StringComparison.Ordinal);
        Assert.Contains("Usage:", result.Error, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Output);
    }

    [Fact]
    public async Task PageOutOfRangeExitsWithOne()
    {
        var result = await RunAsync("text", TestData.Pdf("text-basic.pdf"), "--pages", "9");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("pagewing: Page 9 is out of range (the document has 2 pages).", result.Error.TrimEnd());
        Assert.Equal(string.Empty, result.Output);
    }

    [Fact]
    public async Task NoArgumentsPrintsUsageAndExitsWithTwo()
    {
        var result = await RunAsync();

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Usage", result.Error, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Output);
    }

    [Fact]
    public async Task VersionPrintsOnlyTheVersion()
    {
        var result = await RunAsync("--version");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(AppInfo.Version + "\n", result.Output);
        Assert.Equal(string.Empty, result.Error);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task HelpPrintsToStdoutAndMentionsPasswordVisibility(string flag)
    {
        var result = await RunAsync(flag);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.Contains("Usage:", result.Output, StringComparison.Ordinal);
        Assert.Contains("PAGEWING_PASSWORD", result.Output, StringComparison.Ordinal);
        Assert.Contains("visible to other processes", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CanceledTokenExitsWithOneAndCanceled()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        var result = await RunAsync(environment: null, source.Token, "text", TestData.Pdf("text-basic.pdf"));

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("pagewing: Canceled.", result.Error.TrimEnd());
    }

    [Fact]
    public async Task NonAsciiPathsAndUtf8OutputWork()
    {
        var directory = Path.Combine(workDirectory.FullName, "Pagewing äöü 日本");
        Directory.CreateDirectory(directory);
        var copy = Path.Combine(directory, "größe 日本.pdf");
        File.Copy(TestData.Pdf("text-basic.pdf"), copy);
        var output = Path.Combine(workDirectory.FullName, "Ausgabe äöü 日本");

        var info = await RunAsync("info", copy);
        var render = await RunAsync("render", copy, "--output", output, "--pages", "1");
        var text = await RunAsync("text", copy, "--pages", "2");

        Assert.Equal(0, info.ExitCode);
        Assert.Contains("Pages: 2", info.OutputLines);
        Assert.Contains($"File: {copy}", info.OutputLines);
        Assert.Equal(0, render.ExitCode);
        Assert.Equal([Path.Combine(output, "größe 日本-p001.png")], render.OutputLines);
        Assert.True(File.Exists(Path.Combine(output, "größe 日本-p001.png")));
        Assert.Equal(0, text.ExitCode);

        // Strict UTF-8 without BOM: the bytes are exactly the encoded text.
        Assert.Equal(new UTF8Encoding(false, true).GetBytes("Größe äöü\f"), text.OutputBytes);
    }

    private static Task<CliResult> RunAsync(params string[] args) => RunAsync(environment: null, CancellationToken.None, args);

    private static Task<CliResult> RunAsync(Dictionary<string, string> environment, params string[] args) =>
        RunAsync(environment, CancellationToken.None, args);

    private static async Task<CliResult> RunAsync(Dictionary<string, string>? environment, CancellationToken cancellationToken, params string[] args)
    {
        using var stdout = new MemoryStream();
        using var stderr = new StringWriter();
        var exitCode = await CliApp.RunAsync(
            args,
            stdout,
            stderr,
            name => environment is not null && environment.TryGetValue(name, out var value) ? value : null,
            cancellationToken);
        return new CliResult(exitCode, stdout.ToArray(), stderr.ToString());
    }

    private sealed record CliResult(int ExitCode, byte[] OutputBytes, string Error)
    {
        public string Output { get; } = new UTF8Encoding(false, true).GetString(OutputBytes);

        public string[] OutputLines { get; } = new UTF8Encoding(false, true).GetString(OutputBytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }
}

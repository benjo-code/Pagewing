using System.Text;
using System.Text.RegularExpressions;
using Pagewing.Cli;
using Pagewing.Cli.Commands;
using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Cli;

/// <summary><c>pagewing rotate</c> and the output guard every writing command shares (milestone 13).</summary>
public sealed class RotateCommandTests : IDisposable
{
    /// <summary>User password of the encrypted PDFium test files (see <c>testdata/pdfs/LICENSES.md</c>).</summary>
    private const string UserPassword = "hôtel";

    private readonly DirectoryInfo workDirectory = Directory.CreateTempSubdirectory("pagewing-rotate-tests-");
    private readonly PdfEngine engine = new();

    public void Dispose() => workDirectory.Delete(recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RotateTurnsOnlyTheGivenPages()
    {
        var input = Copy("pages-marked.pdf");
        var output = Path.Combine(workDirectory.FullName, "out.pdf");
        var inputBytes = await File.ReadAllBytesAsync(input, Ct);

        var result = await RunAsync("rotate", input, "--pages", "2,4", "--angle", "90", "--output", output);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.Equal([output], result.OutputLines);
        Assert.Equal(inputBytes, await File.ReadAllBytesAsync(input, Ct));
        await using var before = await engine.OpenAsync(input, cancellationToken: Ct);
        await using var after = await engine.OpenAsync(output, cancellationToken: Ct);
        Assert.Equal(before.PageCount, after.PageCount);
        for (var page = 0; page < before.PageCount; page++)
        {
            var turned = page is 1 or 3;
            var oldRotation = await before.GetPageRotationAsync(page, Ct);
            Assert.Equal(turned ? (oldRotation + 90) % 360 : oldRotation, await after.GetPageRotationAsync(page, Ct));
            var oldSize = await before.GetPageSizeAsync(page, Ct);
            var newSize = await after.GetPageSizeAsync(page, Ct);
            Assert.Equal(turned ? (oldSize.Height, oldSize.Width) : (oldSize.Width, oldSize.Height), (newSize.Width, newSize.Height));
            Assert.Equal(await before.GetTextAsync(page, Ct), await after.GetTextAsync(page, Ct));
        }
    }

    [Theory]
    [InlineData("-90", 270)]
    [InlineData("180", 180)]
    [InlineData("270", 270)]
    public async Task RotateAcceptsTheListedAngles(string angle, int expected)
    {
        var input = Copy("hello_world.pdf");
        var output = Path.Combine(workDirectory.FullName, "out.pdf");

        var result = await RunAsync("rotate", input, "--pages", "all", "--angle", angle, "--output", output);

        Assert.Equal(0, result.ExitCode);
        await using var after = await engine.OpenAsync(output, cancellationToken: Ct);
        Assert.Equal(expected, await after.GetPageRotationAsync(0, Ct));
    }

    [Fact]
    public async Task RotateRefusesExistingOutputWithoutForce()
    {
        var input = Copy("pages-marked.pdf");
        var output = Path.Combine(workDirectory.FullName, "out.pdf");
        await File.WriteAllTextAsync(output, "not a pdf, but mine", Ct);

        var result = await RunAsync("rotate", input, "--pages", "1", "--angle", "90", "--output", output);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("already exists", result.Error, StringComparison.Ordinal);
        Assert.Contains("--force", result.Error, StringComparison.Ordinal);
        Assert.Equal("not a pdf, but mine", await File.ReadAllTextAsync(output, Ct));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task RotateOverwritesExistingOutputWithForce()
    {
        var input = Copy("pages-marked.pdf");
        var output = Path.Combine(workDirectory.FullName, "out.pdf");
        await File.WriteAllTextAsync(output, "old", Ct);

        var result = await RunAsync("rotate", input, "--pages", "1", "--angle", "90", "--output", output, "--force");

        Assert.Equal(0, result.ExitCode);
        await using var after = await engine.OpenAsync(output, cancellationToken: Ct);
        Assert.Equal(6, after.PageCount);
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task RotateRefusesSameInputAndOutput()
    {
        var input = Copy("pages-marked.pdf");
        var inputBytes = await File.ReadAllBytesAsync(input, Ct);
        var spellings = new List<string>
        {
            input,
            Path.Combine(workDirectory.FullName, ".", "pages-marked.pdf"),
            Path.Combine(workDirectory.FullName, "sub", "..", "pages-marked.pdf"),
        };
        if (OperatingSystem.IsWindows())
        {
            spellings.Add(input.ToUpperInvariant());
        }

        foreach (var output in spellings)
        {
            // Not even --force overrides this.
            var result = await RunAsync("rotate", input, "--pages", "1", "--angle", "90", "--output", output, "--force");

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("must not be the input file", result.Error, StringComparison.Ordinal);
            Assert.Equal(inputBytes, await File.ReadAllBytesAsync(input, Ct));
        }

        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task RotateRefusesALinkToTheInputAsTheOutput()
    {
        var input = Copy("pages-marked.pdf");
        var link = Path.Combine(workDirectory.FullName, "link.pdf");
        try
        {
            File.CreateSymbolicLink(link, input);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Creating a symbolic link needs a privilege on Windows; without it there is nothing to check.
            return;
        }

        var inputBytes = await File.ReadAllBytesAsync(input, Ct);

        var result = await RunAsync("rotate", input, "--pages", "1", "--angle", "90", "--output", link, "--force");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("must not be the input file", result.Error, StringComparison.Ordinal);
        Assert.Equal(inputBytes, await File.ReadAllBytesAsync(input, Ct));
    }

    [Fact]
    public async Task RotateRefusesADirectoryAsTheOutput()
    {
        var input = Copy("pages-marked.pdf");

        var result = await RunAsync("rotate", input, "--pages", "1", "--angle", "90", "--output", workDirectory.FullName, "--force");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("is a directory", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RotateNamesAMissingOutputFolder()
    {
        var input = Copy("pages-marked.pdf");
        var output = Path.Combine(workDirectory.FullName, "missing", "out.pdf");

        var result = await RunAsync("rotate", input, "--pages", "1", "--angle", "90", "--output", output);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("does not exist", result.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(workDirectory.FullName, "missing")));
    }

    [Fact]
    public async Task RotateKeepsOutlineAndLabels()
    {
        var input = Copy("pages-marked.pdf");
        var output = Path.Combine(workDirectory.FullName, "out.pdf");

        var result = await RunAsync("rotate", input, "--pages", "all", "--angle", "180", "--output", output);

        Assert.Equal(0, result.ExitCode);

        // A rotation keeps every page where it was, so nothing is lost and nothing is announced.
        Assert.Equal(string.Empty, result.Error);
        await using var before = await engine.OpenAsync(input, cancellationToken: Ct);
        await using var after = await engine.OpenAsync(output, cancellationToken: Ct);
        var oldOutline = await before.GetOutlineAsync(Ct);
        var newOutline = await after.GetOutlineAsync(Ct);
        Assert.NotEmpty(oldOutline);
        Assert.Equal(Titles(oldOutline), Titles(newOutline));
        Assert.True(await after.HasPageLabelsAsync(Ct));
    }

    [Theory]
    [InlineData("encrypted_hello_world_r3.pdf")]
    [InlineData("encrypted_hello_world_r6.pdf")]
    public async Task RotateOfEncryptedFileNeedsThePasswordAndKeepsEncryption(string fixture)
    {
        var input = Copy(fixture);
        var output = Path.Combine(workDirectory.FullName, "out.pdf");

        var without = await RunAsync("rotate", input, "--pages", "1", "--angle", "90", "--output", output);
        Assert.Equal(1, without.ExitCode);
        Assert.Contains("Password required", without.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(output));

        var wrong = await RunAsync("rotate", input, "--pages", "1", "--angle", "90", "--output", output, "--password", "wrong");
        Assert.Equal(1, wrong.ExitCode);
        Assert.False(File.Exists(output));

        var withPassword = await RunAsync(
            new Dictionary<string, string> { ["PAGEWING_PASSWORD"] = UserPassword },
            "rotate",
            input,
            "--pages",
            "1",
            "--angle",
            "90",
            "--output",
            output);

        Assert.Equal(0, withPassword.ExitCode);
        Assert.Equal(string.Empty, withPassword.Error);
        var locked = await Assert.ThrowsAsync<PdfOpenException>(() => engine.OpenAsync(output, cancellationToken: Ct));
        Assert.Equal(PdfOpenError.PasswordRequired, locked.Error);
        await using var opened = await engine.OpenAsync(output, UserPassword, Ct);
        Assert.Equal(90, await opened.GetPageRotationAsync(0, Ct));
        Assert.Empty(TemporaryFiles());
    }

    [Theory]
    [InlineData(2, "--angle", "45")]
    [InlineData(2, "--angle", "abc")]
    [InlineData(2, "--angle", "0")]
    [InlineData(2, "--angle", "-180")]
    [InlineData(2, "--pages", "5-3")]
    [InlineData(2, "--pages", "0")]
    [InlineData(2, "--pages", "abc")]
    [InlineData(1, "--pages", "9")]
    [InlineData(1, "--pages", "1-7")]
    public async Task RotateRejectsBadAngleAndRange(int exitCode, string option, string value)
    {
        var input = Copy("pages-marked.pdf");
        var output = Path.Combine(workDirectory.FullName, "out.pdf");
        var args = new List<string> { "rotate", input, "--output", output };
        args.AddRange(option == "--angle" ? ["--pages", "1", "--angle", value] : ["--angle", "90", "--pages", value]);

        var result = await RunAsync(args.ToArray());

        Assert.Equal(exitCode, result.ExitCode);
        Assert.StartsWith("pagewing: ", result.Error, StringComparison.Ordinal);
        Assert.Contains(option == "--angle" ? "--angle" : exitCode == 1 ? "out of range" : "--pages", result.Error, StringComparison.Ordinal);
        Assert.Equal(exitCode == 2, result.Error.Contains("Usage:", StringComparison.Ordinal));
        Assert.False(File.Exists(output));
        Assert.Empty(TemporaryFiles());
    }

    [Theory]
    [InlineData("--pages")]
    [InlineData("--angle")]
    [InlineData("--output")]
    public async Task RotateNeedsPagesAngleAndOutput(string missing)
    {
        var input = Copy("pages-marked.pdf");
        var output = Path.Combine(workDirectory.FullName, "out.pdf");
        var args = new List<string> { "rotate", input };
        foreach (var (name, value) in new[] { ("--pages", "1"), ("--angle", "90"), ("--output", output) })
        {
            if (name != missing)
            {
                args.AddRange([name, value]);
            }
        }

        var result = await RunAsync(args.ToArray());

        Assert.Equal(2, result.ExitCode);
        Assert.Contains(missing, result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData("signed-docmdp-none.pdf")]
    [InlineData("signed-docmdp-1.pdf")]
    [InlineData("signed-docmdp-2.pdf")]
    [InlineData("signed-docmdp-3.pdf")]
    public async Task RotateOfSignedFilePrintsTheWarningOnStderr(string fixture)
    {
        var input = Copy(fixture);
        var output = Path.Combine(workDirectory.FullName, "out.pdf");
        bool? outputExistedWhenWarned = null;
        using var stderr = new ProbeWriter(() => outputExistedWhenWarned ??= File.Exists(output));

        var result = await RunAsync(stderr, environment: null, CancellationToken.None, "rotate", input, "--pages", "1", "--angle", "90", "--output", output);

        // A warning does not block: the file is written, but the user was told first.
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("warning: ", result.Error, StringComparison.Ordinal);
        Assert.Contains("digitally signed", result.Error, StringComparison.Ordinal);
        Assert.Contains("shown as invalid", result.Error, StringComparison.Ordinal);
        Assert.False(outputExistedWhenWarned);
        Assert.True(File.Exists(output));
        Assert.DoesNotMatch(new Regex(@"\b(valid|legally|binding)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5)), result.Error);
    }

    [Fact]
    public async Task CancelLeavesNoTemporaryFile()
    {
        var input = Copy("signed-docmdp-1.pdf");
        var output = Path.Combine(workDirectory.FullName, "out.pdf");

        // Ctrl+C right after the warning, when the command is about to write.
        using var source = new CancellationTokenSource();
        using var stderr = new ProbeWriter(() => source.Cancel());
        var midway = await RunAsync(stderr, environment: null, source.Token, "rotate", input, "--pages", "1", "--angle", "90", "--output", output);

        using var already = new CancellationTokenSource();
        await already.CancelAsync();
        var before = await RunAsync(environment: null, already.Token, "rotate", input, "--pages", "1", "--angle", "90", "--output", output);

        Assert.Equal(1, midway.ExitCode);
        Assert.Contains("pagewing: Canceled.", midway.Error, StringComparison.Ordinal);
        Assert.Equal(1, before.ExitCode);
        Assert.Contains("pagewing: Canceled.", before.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task HelpAndUsageListRotate()
    {
        var result = await RunAsync("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("pagewing rotate <file> --pages <range> --angle <90|180|270|-90> --output <out.pdf> [--force] [--password <pw>]", result.Output, StringComparison.Ordinal);
        Assert.Contains("--force", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--force")]
    [InlineData("--angle")]
    public async Task OptionsOfRotateAreRefusedByTheReadingCommands(string option)
    {
        var result = await RunAsync("info", Copy("pages-marked.pdf"), option);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains($"Unknown option '{option}' for 'info'", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void OutputGuardChecksPathsAndExistingFiles()
    {
        var input = Copy("pages-marked.pdf");
        var other = Path.Combine(workDirectory.FullName, "other.pdf");

        OutputGuard.Check(input, other, force: false);
        Assert.Throws<CliException>(() => OutputGuard.Check(input, input, force: true));
        File.WriteAllText(other, "x");
        Assert.Throws<CliException>(() => OutputGuard.Check(input, other, force: false));
        OutputGuard.Check(input, other, force: true);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void EveryKindOfLossHasWordingThatNeverPromisesValidity(PageLossKind kind)
    {
        foreach (var detail in new string?[] { null, PageLossDetail.Source(2), PageLossDetail.DocMdp(2), PageLossDetail.Duplicated, PageLossDetail.Inserted, PageLossDetail.Unchecked, PageLossDetail.DeletedPage, PageLossDetail.CopiedPage, PageLossDetail.Shifted })
        {
            foreach (var blocks in new[] { false, true })
            {
                var text = PageLossText.Describe(new PageLoss(kind, detail, [0, 1, 2, 5], blocks));

                Assert.False(string.IsNullOrWhiteSpace(text));
                Assert.DoesNotContain('\n', text);
                Assert.DoesNotMatch(new Regex(@"\b(valid|legally|binding)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5)), text);
            }
        }
    }

    [Fact]
    public void PageListsAreShownOneBasedAndInRanges()
    {
        Assert.Equal("1", PageLossText.FormatPages([0]));
        Assert.Equal("1-3, 6, 8-9", PageLossText.FormatPages([0, 1, 2, 5, 7, 8]));
    }

    public static TheoryData<PageLossKind> AllKinds()
    {
        var data = new TheoryData<PageLossKind>();
        foreach (var kind in Enum.GetValues<PageLossKind>())
        {
            data.Add(kind);
        }

        return data;
    }

    private static string[] Titles(IReadOnlyList<PdfOutlineItem> items) =>
        [.. items.SelectMany(static item => new[] { item.Title }.Concat(Titles(item.Children)))];

    private string Copy(string fixture)
    {
        var path = Path.Combine(workDirectory.FullName, fixture);
        File.Copy(TestData.Pdf(fixture), path, overwrite: true);
        return path;
    }

    private string[] TemporaryFiles() => Directory.GetFiles(workDirectory.FullName, ".pw-*");

    private static Task<CliResult> RunAsync(params string[] args) => RunAsync(new StringWriter(), environment: null, CancellationToken.None, args);

    private static Task<CliResult> RunAsync(Dictionary<string, string>? environment, CancellationToken cancellationToken, params string[] args) =>
        RunAsync(new StringWriter(), environment, cancellationToken, args);

    private static Task<CliResult> RunAsync(Dictionary<string, string> environment, params string[] args) =>
        RunAsync(new StringWriter(), environment, CancellationToken.None, args);

    private static async Task<CliResult> RunAsync(StringWriter stderr, Dictionary<string, string>? environment, CancellationToken cancellationToken, params string[] args)
    {
        using var stdout = new MemoryStream();
        var exitCode = await CliApp.RunAsync(
            args,
            stdout,
            stderr,
            name => environment is not null && environment.TryGetValue(name, out var value) ? value : null,
            cancellationToken);
        return new CliResult(exitCode, stdout.ToArray(), stderr.ToString());
    }

    private sealed class ProbeWriter(Action onFirstWrite) : StringWriter
    {
        private bool first = true;

        public override void WriteLine(string? value)
        {
            Probe();
            base.WriteLine(value);
        }

        public override void Write(string? value)
        {
            Probe();
            base.Write(value);
        }

        private void Probe()
        {
            if (first)
            {
                first = false;
                onFirstWrite();
            }
        }
    }

    private sealed record CliResult(int ExitCode, byte[] OutputBytes, string Error)
    {
        public string Output { get; } = new UTF8Encoding(false, true).GetString(OutputBytes);

        public string[] OutputLines { get; } = new UTF8Encoding(false, true).GetString(OutputBytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }
}

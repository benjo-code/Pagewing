using System.Text.RegularExpressions;
using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;
using Xunit;

namespace Pagewing.Core.Tests.Cli;

/// <summary><c>pagewing merge</c> (milestone 13, spec 5.6): several files into one, the options after a file belonging to that file.</summary>
public sealed class MergeCommandTests : CliTestBase
{
    private string Output => Path.Combine(WorkDirectory.FullName, "merged.pdf");

    [Fact]
    public async Task MergeConcatenatesFilesWithRangesPerFile()
    {
        var first = Copy("pages-marked.pdf", "first.pdf");
        var second = Copy("pages-marked.pdf", "second.pdf");
        var third = Copy("text-rich.pdf", "third.pdf");
        var thirdBytes = await File.ReadAllBytesAsync(third, Ct);

        // --pages belongs to the file before it; the third file has none, so all of its pages come.
        var result = await RunAsync("merge", "--output", Output, first, "--pages", "1,3", second, "--pages", "5-6", third);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([Output], result.OutputLines);
        var texts = await PageTextsAsync(Output);
        Assert.Equal(6, texts.Length);
        Assert.Contains("PAGE 1", texts[0], StringComparison.Ordinal);
        Assert.Contains("PAGE 3", texts[1], StringComparison.Ordinal);
        Assert.Contains("PAGE 5", texts[2], StringComparison.Ordinal);
        Assert.Contains("PAGE 6", texts[3], StringComparison.Ordinal);
        Assert.Equal(await PageTextsAsync(third), texts[4..]);
        Assert.Equal(thirdBytes, await File.ReadAllBytesAsync(third, Ct));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task MergeKeepsTheInputsAsTheyWereAndTakesRangesOfTheFirstFileToo()
    {
        var first = Copy("pages-marked.pdf", "first.pdf");
        var second = Copy("text-rich.pdf", "second.pdf");
        var firstBytes = await File.ReadAllBytesAsync(first, Ct);

        // The first file's own --pages is respected: pages 2 and 4 only.
        var result = await RunAsync("merge", first, "--pages", "2,4", second, "--output", Output);

        Assert.Equal(0, result.ExitCode);
        var texts = await PageTextsAsync(Output);
        Assert.Equal(4, texts.Length);
        Assert.Contains("PAGE 2", texts[0], StringComparison.Ordinal);
        Assert.Contains("PAGE 4", texts[1], StringComparison.Ordinal);
        Assert.Equal(firstBytes, await File.ReadAllBytesAsync(first, Ct));
    }

    [Fact]
    public async Task MergeAllowsTheSameFileTwiceAsASource()
    {
        var input = Copy("pages-marked.pdf");

        var result = await RunAsync("merge", input, "--pages", "1", input, "--pages", "2", "--output", Output);

        Assert.Equal(0, result.ExitCode);
        var texts = await PageTextsAsync(Output);
        Assert.Equal(2, texts.Length);
        Assert.Contains("PAGE 1", texts[0], StringComparison.Ordinal);
        Assert.Contains("PAGE 2", texts[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task MergeKeepsOutlineOfTheFirstFileAndPrintsTheLossOfTheOthers()
    {
        var first = Copy("pages-marked.pdf", "main.pdf");
        var second = Copy("outline.pdf", "extra.pdf");
        bool? outputExistedWhenWarned = null;
        using var stderr = new ProbeWriter(() => outputExistedWhenWarned ??= File.Exists(Output));

        var result = await RunAsync(stderr, environment: null, CancellationToken.None, "merge", "--output", Output, first, second);

        Assert.Equal(0, result.ExitCode);
        await using var before = await Engine.OpenAsync(first, cancellationToken: Ct);
        await using var after = await Engine.OpenAsync(Output, cancellationToken: Ct);
        var wanted = Titles(await before.GetOutlineAsync(Ct));
        Assert.NotEmpty(wanted);
        Assert.Equal(wanted, Titles(await after.GetOutlineAsync(Ct)));

        // What the second file loses is said, by its name, and before the output exists.
        Assert.Contains("warning: The bookmarks of inserted file '" + second + "' are not taken over.", result.Error, StringComparison.Ordinal);
        Assert.False(outputExistedWhenWarned);
        Assert.True(File.Exists(Output));
        Assert.DoesNotMatch(new Regex(@"\b(valid|legally|binding)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5)), result.Error.Replace("shown as invalid", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public async Task MergeWarnsAboutFormFieldNameCollisions()
    {
        var first = Copy("forms-basic.pdf", "one.pdf");
        var second = Copy("forms-basic.pdf", "two.pdf");

        // The command is stopped right after the warnings, so the test does not write pages with form fields: PDFium crashes now
        // and then (an access violation in FPDF_SaveAsCopy) when it saves a document into which pages with widgets were imported;
        // see the report of task 14. The warning is what is asserted here, and it comes before the write.
        using var source = new CancellationTokenSource();
        using var stderr = new ProbeWriter(() => source.Cancel());

        var result = await RunAsync(stderr, environment: null, source.Token, "merge", "--output", Output, first, second);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("pagewing: Canceled.", result.Error, StringComparison.Ordinal);
        Assert.Contains("warning: Form fields of the same name come from different files.", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
        Assert.Empty(TemporaryFiles());
    }

    [Theory]
    [InlineData("only-output")]
    [InlineData("one-file")]
    [InlineData("no-output")]
    [InlineData("no-files-no-output")]
    [InlineData("pages-before-a-file")]
    [InlineData("password-before-a-file")]
    [InlineData("pages-twice")]
    [InlineData("unknown-option")]
    [InlineData("option-of-another-command")]
    [InlineData("bad-range")]
    [InlineData("output-twice")]
    [InlineData("option-without-value")]
    public async Task MergeNeedsAtLeastTwoFilesAndAnOutput(string mistake)
    {
        var a = Copy("hello_world.pdf", "a.pdf");
        var b = Copy("text-basic.pdf", "b.pdf");
        string[] args = mistake switch
        {
            "only-output" => ["merge", "--output", Output],
            "one-file" => ["merge", "--output", Output, a],
            "no-output" => ["merge", a, b],
            "no-files-no-output" => ["merge"],
            "pages-before-a-file" => ["merge", "--pages", "1", a, b, "--output", Output],
            "password-before-a-file" => ["merge", "--password", "x", a, b, "--output", Output],
            "pages-twice" => ["merge", a, "--pages", "1", "--pages", "1", b, "--output", Output],
            "unknown-option" => ["merge", a, b, "--output", Output, "--frobnicate"],
            "option-of-another-command" => ["merge", a, b, "--output", Output, "--angle", "90"],
            "bad-range" => ["merge", a, "--pages", "5-3", b, "--output", Output],
            "output-twice" => ["merge", a, b, "--output", Output, "--output", Output],
            _ => ["merge", a, b, "--output"],
        };

        var result = await RunAsync(args);

        Assert.Equal(2, result.ExitCode);
        Assert.StartsWith("pagewing: ", result.Error, StringComparison.Ordinal);
        Assert.Contains("Usage:", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public async Task MergeRefusesExistingOutputWithoutForce()
    {
        var a = Copy("hello_world.pdf", "a.pdf");
        var b = Copy("text-basic.pdf", "b.pdf");
        await File.WriteAllTextAsync(Output, "mine", Ct);

        var refused = await RunAsync("merge", "--output", Output, a, b);

        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("already exists", refused.Error, StringComparison.Ordinal);
        Assert.Contains("--force", refused.Error, StringComparison.Ordinal);
        Assert.Equal("mine", await File.ReadAllTextAsync(Output, Ct));
        Assert.Empty(TemporaryFiles());

        var forced = await RunAsync("merge", "--output", Output, a, b, "--force");

        Assert.Equal(0, forced.ExitCode);
        await using var written = await Engine.OpenAsync(Output, cancellationToken: Ct);
        Assert.Equal(3, written.PageCount);
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task MergeNeverWritesOverAnInputNotEvenWithForce()
    {
        var a = Copy("hello_world.pdf", "a.pdf");
        var b = Copy("text-basic.pdf", "b.pdf");
        var bBytes = await File.ReadAllBytesAsync(b, Ct);

        // The guard looks at every input, not only the first.
        var result = await RunAsync("merge", a, b, "--output", b, "--force");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("must not be the input file", result.Error, StringComparison.Ordinal);
        Assert.Equal(bBytes, await File.ReadAllBytesAsync(b, Ct));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task MergeRefusesAnOutputThatIsAnInputThroughALinkedFolder()
    {
        var real = Folder("real");
        var first = Copy("hello_world.pdf", "a.pdf");
        var second = Path.Combine(real, "second.pdf");
        File.Copy(TestData(), second);
        var linked = Path.Combine(WorkDirectory.FullName, "linked");
        Assert.True(CreateFolderLink(linked, real), "A folder link could not be created.");
        var before = await File.ReadAllBytesAsync(second, Ct);

        var result = await RunAsync("merge", first, second, "--output", Path.Combine(linked, "second.pdf"), "--force");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("must not be the input file", result.Error, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(second, Ct));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task MergeRefusesAHardLinkToAnInputAsTheOutput()
    {
        var a = Copy("hello_world.pdf", "a.pdf");
        var b = Copy("text-basic.pdf", "b.pdf");
        var alias = Path.Combine(WorkDirectory.FullName, "alias.pdf");
        Assert.SkipUnless(TryCreate(() => CreateHardLink(alias, b)), "Hard links could not be created on this file system.");
        var before = await File.ReadAllBytesAsync(b, Ct);

        var result = await RunAsync("merge", a, b, "--output", alias, "--force");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("must not be the input file", result.Error, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(b, Ct));
    }

    [Fact]
    public async Task MergeWithWrongPasswordNamesTheFile()
    {
        var plain = Copy("hello_world.pdf", "plain.pdf");
        var locked = Copy("encrypted_hello_world_r3.pdf", "locked.pdf");

        var missing = await RunAsync("merge", plain, locked, "--output", Output);
        var wrong = await RunAsync("merge", plain, locked, "--password", "wrong", "--output", Output);

        Assert.Equal(1, missing.ExitCode);
        Assert.Contains($"Password required for '{locked}'", missing.Error, StringComparison.Ordinal);
        Assert.Equal(1, wrong.ExitCode);
        Assert.Contains($"The password for '{locked}' is incorrect.", wrong.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong", wrong.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task MergeTakesThePasswordOfEachFileOrTheEnvironment()
    {
        var plain = Copy("hello_world.pdf", "plain.pdf");
        var r3 = Copy("encrypted_hello_world_r3.pdf", "r3.pdf");
        var r6 = Copy("encrypted_hello_world_r6.pdf", "r6.pdf");

        // The option belongs to its file; the environment variable serves every file that has none.
        var byOption = await RunAsync("merge", r3, "--password", UserPassword, r6, "--password", UserPassword, plain, "--output", Output);
        File.Delete(Output);
        var byEnvironment = await RunAsync(
            new Dictionary<string, string> { ["PAGEWING_PASSWORD"] = UserPassword },
            "merge",
            r3,
            r6,
            plain,
            "--output",
            Output);

        Assert.Equal(0, byOption.ExitCode);
        Assert.Equal(0, byEnvironment.ExitCode);
        Assert.DoesNotContain(UserPassword, byOption.Error + byOption.Output + byEnvironment.Error + byEnvironment.Output, StringComparison.Ordinal);
        await using var opened = await Engine.OpenAsync(Output, UserPassword, Ct);
        Assert.Equal(3, opened.PageCount);
    }

    [Theory]
    [InlineData("encrypted_hello_world_r3.pdf")]
    [InlineData("encrypted_hello_world_r6.pdf")]
    public async Task MergeOfAnEncryptedFirstFileStaysEncrypted(string fixture)
    {
        var locked = Copy(fixture, "locked.pdf");
        var plain = Copy("hello_world.pdf", "plain.pdf");

        var result = await RunAsync("merge", locked, "--password", UserPassword, plain, "--output", Output);

        Assert.Equal(0, result.ExitCode);
        var closed = await Assert.ThrowsAsync<PdfOpenException>(() => Engine.OpenAsync(Output, cancellationToken: Ct));
        Assert.Equal(PdfOpenError.PasswordRequired, closed.Error);
        await using var opened = await Engine.OpenAsync(Output, UserPassword, Ct);
        Assert.Equal(2, opened.PageCount);
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task MergeRefusesAnEncryptedFileInsertedIntoAPlainOneAndWritesNothing()
    {
        var plain = Copy("hello_world.pdf", "plain.pdf");
        var locked = Copy("encrypted_hello_world_r3.pdf", "locked.pdf");

        var result = await RunAsync("merge", plain, locked, "--password", UserPassword, "--output", Output);

        // It would write the pages of a protected file without protection: refused, with the reason.
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("cannot be applied", result.Error, StringComparison.Ordinal);
        Assert.Contains("is encrypted", result.Error, StringComparison.Ordinal);
        Assert.Contains(locked, result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task MergeRefusesPagesBeyondTheEndNamingTheFile()
    {
        var a = Copy("pages-marked.pdf", "a.pdf");
        var b = Copy("hello_world.pdf", "b.pdf");

        var result = await RunAsync("merge", a, b, "--pages", "2", "--output", Output);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"'{b}'", result.Error, StringComparison.Ordinal);
        Assert.Contains("Page 2 is out of range", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public async Task MergeRefusesTheTwentyFirstOtherFileBeforeOpeningAnything()
    {
        var first = Copy("hello_world.pdf", "first.pdf");
        var others = Enumerable.Range(0, PageEditLimits.MaxInsertedFiles + 1)
            .Select(i => Copy("hello_world.pdf", $"other{i}.pdf"))
            .ToArray();

        var refused = await RunAsync(["merge", "--output", Output, first, .. others]);
        var allowed = await RunAsync(["merge", "--output", Output, first, .. others.Take(PageEditLimits.MaxInsertedFiles)]);

        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("at most 20 other files", refused.Error, StringComparison.Ordinal);
        Assert.Equal(0, allowed.ExitCode);
        await using var written = await Engine.OpenAsync(Output, cancellationToken: Ct);
        Assert.Equal(1 + PageEditLimits.MaxInsertedFiles, written.PageCount);
    }

    [Fact]
    public async Task MergeNamesAFileThatDoesNotExist()
    {
        var a = Copy("hello_world.pdf", "a.pdf");
        var missing = Path.Combine(WorkDirectory.FullName, "missing.pdf");

        var result = await RunAsync("merge", a, missing, "--output", Output);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(missing, result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public async Task MergeStopsWhenAnInsertedFileChangesWhileItRuns()
    {
        var a = Copy("pages-marked.pdf", "a.pdf");
        var b = Copy("text-basic.pdf", "b.pdf");

        // Another program touches the second file after it was read and before the first byte is written: the warnings are the
        // last thing before the write (the primary has page labels, which the insertion shifts, so there is a warning).
        var touched = false;
        using var stderr = new ProbeWriter(() =>
        {
            touched = true;
            File.AppendAllText(b, "\n% changed by another program\n");
        });

        var result = await RunAsync(stderr, environment: null, CancellationToken.None, "merge", a, b, "--output", Output);

        Assert.True(touched);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"'{b}'", result.Error, StringComparison.Ordinal);
        Assert.Contains("changed", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task MergeNeverReplacesAFileThatAppearsWhileTheOutputIsWritten()
    {
        var a = Copy("pages-marked.pdf", "a.pdf");
        var b = Copy("text-basic.pdf", "b.pdf");
        var foreign = "foreign file of another program"u8.ToArray();
        var progress = new CallbackProgress(value =>
        {
            if (value > 0.3 && !File.Exists(Output))
            {
                File.WriteAllBytes(Output, foreign);
            }
        });

        var result = await RunWithProgressAsync(progress, Ct, "merge", a, b, "--output", Output);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("already exists. Use --force to replace it.", result.Error, StringComparison.Ordinal);
        Assert.Equal(foreign, await File.ReadAllBytesAsync(Output, Ct));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task CtrlCCancelsWithoutTemporaryFiles()
    {
        var a = Copy("pages-marked.pdf", "a.pdf");
        var b = Copy("text-rich.pdf", "b.pdf");

        // Ctrl+C from inside the saver: the build phase reports up to 0.3, only the saver goes beyond. The temporary file,
        // which only the saver creates, must exist at that moment, or the test proves nothing.
        using var source = new CancellationTokenSource();
        bool? temporaryExistedAtCancel = null;
        var progress = new CallbackProgress(value =>
        {
            if (value > 0.3 && temporaryExistedAtCancel is null)
            {
                temporaryExistedAtCancel = TemporaryFiles().Length > 0;
                source.Cancel();
            }
        });

        var result = await RunWithProgressAsync(progress, source.Token, "merge", a, b, "--output", Output);

        Assert.True(temporaryExistedAtCancel, "The cancel must come from inside the saver (its temporary file exists).");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("pagewing: Canceled.", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task CancelRightAfterTheWarningsWritesNothing()
    {
        var a = Copy("pages-marked.pdf", "a.pdf");
        var b = Copy("outline.pdf", "b.pdf");
        using var source = new CancellationTokenSource();
        using var stderr = new ProbeWriter(() => source.Cancel());

        var result = await RunAsync(stderr, environment: null, source.Token, "merge", a, b, "--output", Output);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("pagewing: Canceled.", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task HelpTextListsTheNewCommands()
    {
        var result = await RunAsync("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("pagewing merge  --output <out.pdf> <file> [--pages <range>] [--password <pw>] <file> [--pages <range>] [--password <pw>] ... [--force]", result.Output, StringComparison.Ordinal);
        Assert.Contains("pagewing split  <file> --output <dir> (--every <n> | --ranges <ranges> | --bookmarks) [--force] [--password <pw>]", result.Output, StringComparison.Ordinal);
        Assert.Contains("pagewing export <file> [--pages <range>] [--dpi <n>] [--output <dir>] [--no-annotations] [--force] [--password <pw>]", result.Output, StringComparison.Ordinal);
        foreach (var word in new[] { "merge   ", "split   ", "export  ", "--every <n>", "--ranges <list>", "--bookmarks", "Examples:", "pagewing merge --output" })
        {
            Assert.Contains(word, result.Output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("info", "--every", "2")]
    [InlineData("render", "--force")]
    [InlineData("text", "--ranges", "1-2")]
    [InlineData("search", "--bookmarks")]
    [InlineData("rotate", "--every", "2")]
    [InlineData("export", "--angle", "90")]
    [InlineData("split", "--pages", "1")]
    [InlineData("split", "--dpi", "72")]
    public async Task OptionsAreValidPerCommandOnly(params string[] args)
    {
        var input = Copy("pages-marked.pdf");
        string[] full = args[0] == "search" ? [args[0], input, "q", .. args[1..]] : [args[0], input, .. args[1..]];

        var result = await RunAsync(full);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains($"for '{args[0]}'", result.Error, StringComparison.Ordinal);
    }

    private static string TestData() => Pagewing.TestSupport.TestData.Pdf("text-basic.pdf");
}

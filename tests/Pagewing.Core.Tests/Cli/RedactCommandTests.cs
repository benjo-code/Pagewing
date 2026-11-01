using System.Text.RegularExpressions;
using Pagewing.Cli;
using Pagewing.Cli.Commands;
using Pagewing.Core.Pdf;
using Pagewing.Core.Tests.Redaction;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Cli;

/// <summary><c>pagewing redact</c> (milestone 14): search, rectangles, the report on stderr and the check that leaves no file when it finds anything.</summary>
public sealed class RedactCommandTests : CliTestBase
{
    private const string Term = "Zephyrstraße 17";

    /// <summary>
    /// The note of the fixture together with its reply, which holds the term too: replies are not searched, and removing the note takes
    /// its reply with it, so a rectangle over both is how a user redacts them (the page would otherwise change outside the marks).
    /// </summary>
    private const string NoteAndReply = "1:248,40,42,22";

    private static readonly Regex ValidWords = new(@"\b(valid|legally|binding)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    private string Output => Path.Combine(WorkDirectory.FullName, "out.pdf");

    /// <summary>Two pages that both hold the same text, as <c>redact-everywhere</c> in miniature but without annotations or metadata.</summary>
    private string TwoPages(string first = "secret value", string second = "secret value")
    {
        var pdf = new VerifierPdf($"BT /F1 12 Tf 20 150 Td ({first}) Tj ET BT /F1 12 Tf 20 100 Td (public line) Tj ET", $"BT /F1 12 Tf 20 150 Td ({second}) Tj ET BT /F1 12 Tf 20 100 Td (public line) Tj ET");
        var path = Path.Combine(WorkDirectory.FullName, "two.pdf");
        File.WriteAllBytes(path, pdf.Build());
        return path;
    }

    [Fact]
    public async Task RedactRemovesTheTextAndPrintsTheReportOnStderr()
    {
        var input = Copy("redact-everywhere-nopattern.pdf");
        var before = await File.ReadAllBytesAsync(input, Ct);

        var result = await RunAsync("redact", input, "--output", Output, "--text", Term, "--rect", NoteAndReply);

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Equal([Output], result.OutputLines);
        Assert.Equal(before, await File.ReadAllBytesAsync(input, Ct));
        Assert.Contains("Removed:", result.Error, StringComparison.Ordinal);
        Assert.Contains("Cleaned (read back from the written file):", result.Error, StringComparison.Ordinal);
        Assert.Contains("Checked:", result.Error, StringComparison.Ordinal);
        Assert.Contains("Not checked:", result.Error, StringComparison.Ordinal);

        // What was not searched is told on every run with --text.
        Assert.Contains("Not searched:", result.Error, StringComparison.Ordinal);
        foreach (var line in Pagewing.Core.Redaction.RedactionSearch.NotSearched)
        {
            Assert.Contains(line, result.Error, StringComparison.Ordinal);
        }

        // The report never contains a removed text.
        Assert.DoesNotContain("Zephyr", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Quellmann", result.Error, StringComparison.Ordinal);
        Assert.DoesNotMatch(ValidWords, result.Error);
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task RedactOutputPassesTheVerifierAgain()
    {
        var input = Copy("redact-everywhere-nopattern.pdf");
        Assert.True(RawPdf.ContainsAnywhere(await File.ReadAllBytesAsync(input, Ct), Term));

        var result = await RunAsync("redact", input, "--output", Output, "--text", Term, "--rect", NoteAndReply);
        Assert.True(result.ExitCode == 0, result.Error);

        // Open the file anew: no page holds the text, and the bytes of the file do not hold it either (strings decoded, streams unpacked).
        foreach (var text in await PageTextsAsync(Output))
        {
            Assert.DoesNotContain("Zephyr", text, StringComparison.Ordinal);
        }

        var bytes = await File.ReadAllBytesAsync(Output, Ct);
        Assert.False(RawPdf.ContainsAnywhere(bytes, Term));
        Assert.False(RawPdf.ContainsAnywhere(bytes, "Quellmann"));
        await using var reopened = await Engine.OpenAsync(Output, cancellationToken: Ct);
        Assert.Equal(2, reopened.PageCount);
        Assert.Contains("Redaction fixture, page one", await reopened.GetTextAsync(0, Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DryRunWritesNothingAndListsMatchesPerPage()
    {
        var input = TwoPages();
        var before = await File.ReadAllBytesAsync(input, Ct);

        var result = await RunAsync("redact", input, "--output", Output, "--text", "secret value", "--dry-run");

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Empty(result.OutputBytes);
        Assert.False(File.Exists(Output));
        Assert.Empty(TemporaryFiles());
        Assert.Equal(before, await File.ReadAllBytesAsync(input, Ct));
        Assert.Contains("Dry run: nothing is written.", result.Error, StringComparison.Ordinal);
        Assert.Contains("Text 1: 2 matches", result.Error, StringComparison.Ordinal);
        Assert.Contains("page 1: 1", result.Error, StringComparison.Ordinal);
        Assert.Contains("page 2: 1", result.Error, StringComparison.Ordinal);
        Assert.Contains("Will be checked after writing", result.Error, StringComparison.Ordinal);

        // More than one match: the matched text is not repeated, only the number and the page.
        Assert.DoesNotContain("secret", result.Error, StringComparison.Ordinal);

        // The output is not needed for a dry run, and it is still checked when it is given: a dry run says what the real run does.
        var withoutOutput = await RunAsync("redact", input, "--text", "secret value", "--dry-run");
        Assert.True(withoutOutput.ExitCode == 0, withoutOutput.Error);
        await File.WriteAllTextAsync(Output, "mine", Ct);
        var existing = await RunAsync("redact", input, "--output", Output, "--text", "secret value", "--dry-run");
        Assert.Equal(1, existing.ExitCode);
        Assert.Contains("already exists", existing.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoMatchIsAnErrorWithExitOne()
    {
        var input = TwoPages();

        var result = await RunAsync("redact", input, "--output", Output, "--text", "secret value", "--text", "not in the file");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("pagewing: No match for \"not in the file\".", result.Error, StringComparison.Ordinal);
        Assert.Contains("Nothing was written.", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
        Assert.Empty(TemporaryFiles());

        // With --pages the sentence says where it looked.
        var limited = await RunAsync("redact", input, "--output", Output, "--text", "public line", "--pages", "1", "--text", "Zzz", "--dry-run");
        Assert.Equal(1, limited.ExitCode);
        Assert.Contains("No match for \"Zzz\" on page 1.", limited.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesExistingOutputWithoutForceAndNeverTheInput()
    {
        var input = TwoPages();
        var inputBytes = await File.ReadAllBytesAsync(input, Ct);
        await File.WriteAllTextAsync(Output, "mine", Ct);

        var without = await RunAsync("redact", input, "--output", Output, "--text", "secret value");
        Assert.Equal(1, without.ExitCode);
        Assert.Contains("already exists", without.Error, StringComparison.Ordinal);
        Assert.Contains("--force", without.Error, StringComparison.Ordinal);
        Assert.Equal("mine", await File.ReadAllTextAsync(Output, Ct));

        // Not even --force writes into the input.
        var same = await RunAsync("redact", input, "--output", input, "--text", "secret value", "--force");
        Assert.Equal(1, same.ExitCode);
        Assert.Contains("must not be the input file", same.Error, StringComparison.Ordinal);
        Assert.Equal(inputBytes, await File.ReadAllBytesAsync(input, Ct));

        // A file that appears while the output is written is not replaced either (the saver's own check), with --force it is.
        File.Delete(Output);
        var foreign = "foreign file of another program"u8.ToArray();
        var progress = new CallbackProgress(value =>
        {
            if (value > 0.5 && !File.Exists(Output))
            {
                File.WriteAllBytes(Output, foreign);
            }
        });
        var raced = await RunWithProgressAsync(progress, Ct, "redact", input, "--output", Output, "--text", "secret value");
        Assert.Equal(1, raced.ExitCode);
        Assert.Contains("already exists. Use --force to replace it.", raced.Error, StringComparison.Ordinal);
        Assert.Equal(foreign, await File.ReadAllBytesAsync(Output, Ct));

        File.Delete(Output);
        var forced = await RunWithProgressAsync(progress, Ct, "redact", input, "--output", Output, "--text", "secret value", "--force");
        Assert.True(forced.ExitCode == 0, forced.Error);
        Assert.NotEqual(foreign, await File.ReadAllBytesAsync(Output, Ct));
        Assert.Empty(TemporaryFiles());
        Assert.Equal(inputBytes, await File.ReadAllBytesAsync(input, Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusesEncryptedWithReason(bool dryRun)
    {
        var input = Copy("encrypted_hello_world_r3.pdf");
        var args = new List<string> { "redact", input, "--output", Output, "--rect", "1:10,10,50,20" };
        if (dryRun)
        {
            args.Add("--dry-run");
        }

        var withoutPassword = await RunAsync([.. args]);
        Assert.Equal(1, withoutPassword.ExitCode);
        Assert.Contains("Password required", withoutPassword.Error, StringComparison.Ordinal);

        var result = await RunAsync(new Dictionary<string, string> { ["PAGEWING_PASSWORD"] = UserPassword }, [.. args]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("pagewing: The file is encrypted.", result.Error, StringComparison.Ordinal);
        Assert.Contains("does not remove protection silently", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
        Assert.Empty(TemporaryFiles());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusesUnremovablePageKeysWithReasonAndPages(bool dryRun)
    {
        var file = new VerifierPdf("BT /F1 12 Tf 20 150 Td (one) Tj ET", "BT /F1 12 Tf 20 150 Td (two) Tj ET");
        var stream = file.AddStream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray", [0x66]);
        file.PageEntriesByPage = ["", $"/Thumb {stream} 0 R"];
        var input = Path.Combine(WorkDirectory.FullName, "thumb.pdf");
        await File.WriteAllBytesAsync(input, file.Build(), Ct);
        var before = await File.ReadAllBytesAsync(input, Ct);
        var args = new List<string> { "redact", input, "--output", Output, "--rect", "1:15,35,45,25" };
        if (dryRun)
        {
            args.Add("--dry-run");
        }

        var result = await RunAsync([.. args]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("A thumbnail, private data, metadata or actions that Pagewing cannot remove are on page 2, so the file is not redacted.", result.Error, StringComparison.Ordinal);
        Assert.Contains("/Thumb", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
        Assert.Empty(TemporaryFiles());
        Assert.Equal(before, await File.ReadAllBytesAsync(input, Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusesPageNotRedactableWithReasonAndPages(bool dryRun)
    {
        // Page one paints with a tiling pattern: PDFium writes the page again without it, so the page would change outside the marks.
        var input = Copy("redact-everywhere.pdf");
        var before = await File.ReadAllBytesAsync(input, Ct);
        var args = new List<string> { "redact", input, "--output", Output, "--text", Term, "--pages", "1" };
        if (dryRun)
        {
            args.Add("--dry-run");
        }

        var result = await RunAsync([.. args]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Redacting page 1 would change content outside the marked areas, so the file is not redacted.", result.Error, StringComparison.Ordinal);
        Assert.Contains("looks different outside the marked areas", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Zephyr", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
        Assert.Empty(TemporaryFiles());
        Assert.Equal(before, await File.ReadAllBytesAsync(input, Ct));
    }

    [Fact]
    public async Task VerificationFailureLeavesNoOutputFile()
    {
        // A mark over the pattern fill lets the page pass the render check, but the pattern is a resource without a page object that
        // PDFium keeps in the file with the text in it: the check of the written file finds it, and the file is not kept.
        var input = Copy("redact-everywhere.pdf");
        var before = await File.ReadAllBytesAsync(input, Ct);

        var result = await RunAsync(
            "redact",
            input,
            "--output",
            Output,
            "--text",
            Term,
            "--rect",
            "1:20,120,200,30",
            "--rect",
            "1:248,40,42,22",
            "--rect",
            "1:20,275,180,25");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.OutputBytes);
        Assert.Contains("is still in the file in a place that is not a page object", result.Error, StringComparison.Ordinal);
        Assert.Contains("the file was not written", result.Error, StringComparison.Ordinal);

        // The report of what was done and what was not checked comes first; it holds no removed text and no "Checked" section.
        Assert.Contains("Removed:", result.Error, StringComparison.Ordinal);
        Assert.Contains("Not checked:", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Checked:", result.Error.Replace("Not checked:", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("Cleaned", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Zephyr", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
        Assert.Empty(TemporaryFiles());
        Assert.Equal(before, await File.ReadAllBytesAsync(input, Ct));
    }

    [Theory]
    [InlineData("signed-docmdp-none.pdf")]
    [InlineData("signed-docmdp-1.pdf")]
    [InlineData("signed-docmdp-2.pdf")]
    [InlineData("signed-docmdp-3.pdf")]
    public async Task SignedFilePrintsTheWarningOnStderr(string fixture)
    {
        var input = Copy(fixture);
        bool? outputExistedWhenWarned = null;
        using var stderr = new ProbeWriter(() => outputExistedWhenWarned ??= File.Exists(Output));

        var result = await RunAsync(stderr, environment: null, CancellationToken.None, "redact", input, "--output", Output, "--rect", "1:20,20,60,20");

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Contains("warning: The file is digitally signed.", result.Error, StringComparison.Ordinal);
        Assert.Contains("shown as invalid", result.Error, StringComparison.Ordinal);
        Assert.False(outputExistedWhenWarned);
        Assert.True(File.Exists(Output));
        Assert.DoesNotMatch(ValidWords, result.Error);
    }

    [Theory]
    [InlineData(2, "--rect", "abc")]
    [InlineData(2, "--rect", "1:1,2,3")]
    [InlineData(2, "--rect", "0:1,2,3,4")]
    [InlineData(2, "--rect", "1:1,2,0,4")]
    [InlineData(2, "--rect", "1:1,2,3,-4")]
    [InlineData(2, "--rect", "1:1,2,3,NaN")]
    [InlineData(2, "--rect", "1:1,2,3,4,5")]
    [InlineData(2, "--rect", "x:1,2,3,4")]
    [InlineData(2, "--text", " ")]
    [InlineData(2, "--pages", "5-3")]
    [InlineData(2, "--pages", "abc")]
    [InlineData(1, "--rect", "3:1,2,30,40")]
    [InlineData(1, "--pages", "9")]
    public async Task BadRectAndBadRangeExitWithMessage(int exitCode, string option, string value)
    {
        var input = TwoPages();
        var args = new List<string> { "redact", input, "--output", Output, "--text", "secret value" };
        args.AddRange([option, value]);

        var result = await RunAsync([.. args]);

        Assert.Equal(exitCode, result.ExitCode);
        Assert.StartsWith("pagewing: ", result.Error, StringComparison.Ordinal);
        Assert.Contains(exitCode == 1 ? "out of range" : option, result.Error, StringComparison.Ordinal);
        Assert.Equal(exitCode == 2, result.Error.Contains("Usage:", StringComparison.Ordinal));
        Assert.False(File.Exists(Output));
        Assert.Empty(TemporaryFiles());
    }

    [Theory]
    [InlineData("redact", "--output")]
    [InlineData("redact", "--text-or-rect")]
    public async Task RedactNeedsAnOutputAndSomethingToRedact(string command, string missing)
    {
        var input = TwoPages();
        var args = missing == "--output" ? new[] { command, input, "--text", "secret value" } : [command, input, "--output", Output];

        var result = await RunAsync(args);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains(missing == "--output" ? "--output" : "--text", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public async Task PagesWithoutTextAndOptionsOfOtherCommandsAreUsageErrors()
    {
        var input = TwoPages();

        var pages = await RunAsync("redact", input, "--output", Output, "--rect", "1:1,2,30,40", "--pages", "1");
        var twice = await RunAsync("redact", input, "--output", Output, "--output", Output, "--text", "secret value");
        var other = await RunAsync("text", input, "--text", "x");
        var dry = await RunAsync("rotate", input, "--pages", "1", "--angle", "90", "--output", Output, "--dry-run");

        Assert.Equal(2, pages.ExitCode);
        Assert.Contains("--pages", pages.Error, StringComparison.Ordinal);
        Assert.Equal(2, twice.ExitCode);
        Assert.Contains("given twice", twice.Error, StringComparison.Ordinal);
        Assert.Equal(2, other.ExitCode);
        Assert.Contains("Unknown option '--text' for 'text'", other.Error, StringComparison.Ordinal);
        Assert.Equal(2, dry.ExitCode);
    }

    [Fact]
    public async Task RectOutsideThePageIsClippedAndReported()
    {
        var input = TwoPages();

        // The page is 300 x 200 pt: a part beyond the edges is cut off and said.
        var clipped = await RunAsync("redact", input, "--output", Output, "--rect", "1:-20,-10,60,50", "--rect", "2:280,180,100,100");

        Assert.True(clipped.ExitCode == 0, clipped.Error);
        Assert.Contains("warning: The rectangle of --rect 1 reaches beyond page 1 (300 x 200 pt) and is clipped to x=0, y=0, w=40, h=40.", clipped.Error, StringComparison.Ordinal);
        Assert.Contains("warning: The rectangle of --rect 2 reaches beyond page 2 (300 x 200 pt) and is clipped to x=280, y=180, w=20, h=20.", clipped.Error, StringComparison.Ordinal);
        Assert.True(File.Exists(Output));

        // A rectangle inside the page is not announced; one that lies wholly outside ends the command.
        File.Delete(Output);
        var inside = await RunAsync("redact", input, "--output", Output, "--rect", "1:10,10,50,40");
        Assert.True(inside.ExitCode == 0, inside.Error);
        Assert.DoesNotContain("clipped", inside.Error, StringComparison.Ordinal);

        File.Delete(Output);
        var outside = await RunAsync("redact", input, "--output", Output, "--rect", "1:1000,1000,10,10");
        Assert.Equal(1, outside.ExitCode);
        Assert.Contains("lies outside page 1 (300 x 200 pt)", outside.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task PagesLimitTextToThosePagesAndMatchCaseKeepsOtherSpellings()
    {
        var input = TwoPages("secret value", "secret value");

        // With --pages only the matches on those pages go; the check holds the rest of the file to "no more than before".
        var onSecond = await RunAsync("redact", input, "--output", Output, "--text", "secret value", "--pages", "2");
        Assert.True(onSecond.ExitCode == 0, onSecond.Error);
        var texts = await PageTextsAsync(Output);
        Assert.Contains("secret value", texts[0], StringComparison.Ordinal);
        Assert.DoesNotContain("secret", texts[1], StringComparison.Ordinal);
        Assert.Contains("public line", texts[1], StringComparison.Ordinal);

        // Without --match-case every spelling found goes; with it only the one given.
        var mixed = TwoPages("Secret Value", "secret value");
        File.Delete(Output);
        var both = await RunAsync("redact", mixed, "--output", Output, "--text", "SECRET VALUE");
        Assert.True(both.ExitCode == 0, both.Error);
        Assert.All(await PageTextsAsync(Output), static text => Assert.DoesNotContain("ecret", text, StringComparison.OrdinalIgnoreCase));

        File.Delete(Output);
        var exact = await RunAsync("redact", mixed, "--output", Output, "--text", "secret value", "--match-case");
        Assert.True(exact.ExitCode == 0, exact.Error);
        var kept = await PageTextsAsync(Output);
        Assert.Contains("Secret Value", kept[0], StringComparison.Ordinal);
        Assert.DoesNotContain("secret", kept[1], StringComparison.OrdinalIgnoreCase);

        // Several terms and a rectangle in one run.
        File.Delete(Output);
        var several = await RunAsync("redact", TwoPages("alpha beta", "gamma"), "--output", Output, "--text", "alpha", "--text", "gamma", "--rect", "1:10,95,60,20");
        Assert.True(several.ExitCode == 0, several.Error);
        var remaining = await PageTextsAsync(Output);
        Assert.DoesNotContain("alpha", remaining[0], StringComparison.Ordinal);
        Assert.Contains("beta", remaining[0], StringComparison.Ordinal);
        Assert.DoesNotContain("gamma", remaining[1], StringComparison.Ordinal);
        Assert.DoesNotContain("public", remaining[0], StringComparison.Ordinal);
        Assert.Contains("public line", remaining[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelLeavesNoTemporaryFile()
    {
        var input = TwoPages();

        // Ctrl+C from inside the saver: the build reports 0 to 0.5, so a value above 0.5 comes from the saver's own stream, and its
        // temporary file exists at that moment.
        using var source = new CancellationTokenSource();
        bool? temporaryExistedAtCancel = null;
        var progress = new CallbackProgress(value =>
        {
            if (value > 0.5 && temporaryExistedAtCancel is null)
            {
                temporaryExistedAtCancel = TemporaryFiles().Length > 0;
                source.Cancel();
            }
        });
        var canceled = await RunWithProgressAsync(progress, source.Token, "redact", input, "--output", Output, "--text", "secret value");

        Assert.True(temporaryExistedAtCancel, "The cancel must come from inside the saver (its temporary file exists), or the test proves nothing.");
        Assert.Equal(1, canceled.ExitCode);
        Assert.Contains("pagewing: Canceled.", canceled.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
        Assert.Empty(TemporaryFiles());

        // Already canceled before the first step.
        using var already = new CancellationTokenSource();
        await already.CancelAsync();
        var before = await RunAsync(new StringWriter(), environment: null, already.Token, "redact", input, "--output", Output, "--text", "secret value");
        Assert.Equal(1, before.ExitCode);
        Assert.Contains("pagewing: Canceled.", before.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task HelpAndUsageListRedact()
    {
        var result = await RunAsync("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("pagewing redact <file> --output <out.pdf> [--text <term>]... [--rect <page>:<x>,<y>,<w>,<h>]... [--pages <range>] [--match-case] [--dry-run] [--force] [--password <pw>]", result.Output, StringComparison.Ordinal);
        Assert.Contains("--dry-run", result.Output, StringComparison.Ordinal);
        Assert.Contains("NOT checked", result.Output, StringComparison.Ordinal);
    }
}

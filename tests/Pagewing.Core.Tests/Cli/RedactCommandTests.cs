using System.Text;
using System.Text.RegularExpressions;
using Pagewing.Cli;
using Pagewing.Cli.Commands;
using Pagewing.Core.Pdf;
using Pagewing.Core.Redaction;
using Pagewing.Core.Tests.Redaction;
using Pagewing.Core.Text;
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

    /// <summary>The pixel (blue, green, red, alpha) at a column and row of the first image of page 1 of a written file.</summary>
    private async Task<byte[]> ImagePixelAsync(string path, int x, int y)
    {
        await using var document = await Engine.OpenAsync(path, cancellationToken: Ct);
        var image = (await document.GetImageAreaPixelsAsync(0, new PageRect(0, 0, 300, 170), Ct))[0];
        var at = ((y * image.Width) + x) * 4;
        return image.Bgra[at..(at + 4)];
    }

    [Fact]
    public async Task CliFillAcceptsBlackWhiteAndHex()
    {
        // The area lies over the first image of the fixture (64 x 64 px at 2 pt per pixel, its lower left corner at 10|16 on a page 170
        // high): the pixels 10 to 30 across and 17 to 37 down are under it. What the command wrote is read back from the output file,
        // not only checked by exit code: an ignored --fill would leave black there.
        var input = Copy("redact-image.pdf");
        var cases = new (string Option, byte[] Expected)[]
        {
            ("black", [0, 0, 0, 255]),
            ("WHITE", [255, 255, 255, 255]),
            ("#336699", [0x99, 0x66, 0x33, 255]),
            ("#aabbcc", [0xCC, 0xBB, 0xAA, 255]),
        };
        foreach (var (option, expected) in cases)
        {
            var output = Path.Combine(WorkDirectory.FullName, $"out-{option.TrimStart('#')}.pdf");
            var result = await RunAsync("redact", input, "--output", output, "--rect", "1:30,60,40,40", "--fill", option);
            Assert.True(result.ExitCode == 0, $"{option}: {result.Error}");
            Assert.Equal(expected, await ImagePixelAsync(output, 20, 27));
            Assert.Equal(expected, await ImagePixelAsync(output, 12, 19));
            Assert.Equal(expected, await ImagePixelAsync(output, 28, 35));
        }

        // Without --fill the areas are black, as before.
        var plain = Path.Combine(WorkDirectory.FullName, "out-plain.pdf");
        Assert.Equal(0, (await RunAsync("redact", input, "--output", plain, "--rect", "1:30,60,40,40")).ExitCode);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, await ImagePixelAsync(plain, 20, 27));

        // Anything else is a usage error (exit 2) and writes nothing.
        foreach (var bad in new[] { "#zz", "#FFF", "#1234567", "red", "336699", string.Empty })
        {
            var output = Path.Combine(WorkDirectory.FullName, "bad.pdf");
            var result = await RunAsync("redact", input, "--output", output, "--rect", "1:30,60,40,40", "--fill", bad);
            Assert.Equal(2, result.ExitCode);
            Assert.Contains("--fill", result.Error, StringComparison.Ordinal);
            Assert.False(File.Exists(output));
        }

        var twice = await RunAsync("redact", input, "--output", Path.Combine(WorkDirectory.FullName, "twice.pdf"), "--rect", "1:30,60,40,40", "--fill", "black", "--fill", "white");
        Assert.Equal(2, twice.ExitCode);
        Assert.Equal(2, (await RunAsync("redact", input, "--output", Path.Combine(WorkDirectory.FullName, "missing.pdf"), "--rect", "1:30,60,40,40", "--fill")).ExitCode);
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

    private static string Utf16(string text) => "<FEFF" + Convert.ToHexString(System.Text.Encoding.BigEndianUnicode.GetBytes(text)) + ">";

    /// <summary>Page one holds the term; page two has a note without a /Rect that holds it too, which the search cannot read (it is reported as not searched).</summary>
    private string UnsearchedPage()
    {
        var pdf = new VerifierPdf("BT /F1 12 Tf 20 150 Td (Zep here) Tj ET", "BT /F1 12 Tf 20 150 Td (Plain) Tj ET");
        var damaged = pdf.Add($"<< /Type /Annot /Subtype /Text /Contents {Utf16("Zep without a rect")} /F 4 >>");
        pdf.PageEntriesByPage = [string.Empty, $"/Annots [{damaged} 0 R]"];
        var path = Path.Combine(WorkDirectory.FullName, "unsearched.pdf");
        File.WriteAllBytes(path, pdf.Build());
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APageThatWasNotSearchedCompletelyStopsTheCommandWithoutTheFlag(bool dryRun)
    {
        var input = UnsearchedPage();
        var before = await File.ReadAllBytesAsync(input, Ct);
        var args = new List<string> { "redact", input, "--output", Output, "--text", "Zep" };
        if (dryRun)
        {
            args.Add("--dry-run");
        }

        var result = await RunAsync([.. args]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("pagewing: Page 2 could not be searched completely, so the text may still be there. Nothing was written.", result.Error, StringComparison.Ordinal);
        Assert.Contains("--allow-unsearched", result.Error, StringComparison.Ordinal);
        Assert.Empty(result.OutputBytes);
        Assert.False(File.Exists(Output));
        Assert.Empty(TemporaryFiles());
        Assert.Equal(before, await File.ReadAllBytesAsync(input, Ct));
    }

    [Fact]
    public async Task AllowUnsearchedGoesOnAndTheReportListsThePagesAsAccepted()
    {
        var input = UnsearchedPage();

        var result = await RunAsync("redact", input, "--output", Output, "--text", "Zep", "--allow-unsearched");

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.True(File.Exists(Output));
        Assert.Contains("warning: page 2 could not be searched completely", result.Error, StringComparison.Ordinal);
        var section = result.Error[result.Error.IndexOf("Not searched (accepted):", StringComparison.Ordinal)..];
        Assert.Contains("- page 2 could not be searched completely; the text may still be there. Accepted with --allow-unsearched.", section, StringComparison.Ordinal);
        foreach (var line in Pagewing.Core.Redaction.RedactionSearch.NotSearched)
        {
            Assert.Contains(line, section, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("Zep", (await PageTextsAsync(Output))[0], StringComparison.Ordinal);

        // The dry run with the flag says the same and writes nothing.
        File.Delete(Output);
        var dry = await RunAsync("redact", input, "--output", Output, "--text", "Zep", "--allow-unsearched", "--dry-run");
        Assert.True(dry.ExitCode == 0, dry.Error);
        Assert.Contains("Not searched (accepted):", dry.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));

        // Without anything unsearched the flag adds no section.
        var complete = await RunAsync("redact", TwoPages(), "--output", Path.Combine(WorkDirectory.FullName, "plain.pdf"), "--text", "secret value", "--allow-unsearched");
        Assert.True(complete.ExitCode == 0, complete.Error);
        Assert.DoesNotContain("accepted", complete.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AMatchThatCannotBeMarkedStopsTheCommand(bool dryRun)
    {
        // A note whose /Rect has no area: the text is found, but there is nothing to mark.
        var pdf = new VerifierPdf("BT /F1 12 Tf 20 150 Td (Plain) Tj ET");
        var flat = pdf.Add($"<< /Type /Annot /Subtype /Text /Rect [50 50 50 50] /Contents {Utf16("Zephyr flat")} /F 4 >>");
        pdf.PageEntries = $"/Annots [{flat} 0 R]";
        var input = Path.Combine(WorkDirectory.FullName, "flat.pdf");
        await File.WriteAllBytesAsync(input, pdf.Build(), Ct);
        var args = new List<string> { "redact", input, "--output", Output, "--text", "Zephyr" };
        if (dryRun)
        {
            args.Add("--dry-run");
        }

        var result = await RunAsync([.. args]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("A match cannot be marked, so the text cannot be removed there and nothing was written:", result.Error, StringComparison.Ordinal);
        Assert.Contains("- page 1 (annotation):", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task AReachedSearchLimitStopsTheCommand()
    {
        // 200 pages with 60 matches each are 12,000 places; the search stops at 10,000 and the text cannot be removed everywhere.
        var content = string.Join(' ', Enumerable.Repeat("x", 60));
        var pdf = new VerifierPdf([.. Enumerable.Range(0, 200).Select(_ => $"BT /F1 6 Tf 5 190 Td ({content}) Tj ET")]);
        var input = Path.Combine(WorkDirectory.FullName, "many.pdf");
        await File.WriteAllBytesAsync(input, pdf.Build(), Ct);

        var result = await RunAsync("redact", input, "--output", Output, "--text", "x");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("pagewing: The search stopped at 10000 matches and more may exist", result.Error, StringComparison.Ordinal);
        Assert.Contains("Nothing was written.", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task DryRunNeverPrintsTheTextOfTheInformationDictionary()
    {
        // The fixture's title and author hold the term; the report of the dry run says they will be cleaned, not what they hold.
        var input = Copy("redact-everywhere-nopattern.pdf");

        var result = await RunAsync("redact", input, "--output", Output, "--text", Term, "--rect", NoteAndReply, "--dry-run");

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Contains("Title in the information dictionary (present; its text is not repeated here).", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Zephyr", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Quellmann", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public async Task HelpAndUsageListRedact()
    {
        var result = await RunAsync("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("pagewing redact <file> --output <out.pdf> [--text <term>]... [--pattern <email|iban|phone|card>]... [--include-unchecked] [--rect <page>:<x>,<y>,<w>,<h>]... [--pages <range>] [--rasterize-pages <range>] [--rasterize-unredactable] [--dpi <72-300>] [--match-case] [--dry-run] [--allow-unsearched] [--fill <black|white|#RRGGBB>] [--image-fill <solid|pixelate|blur>] [--image-strength <coarse|very-coarse>] [--overlay-text <text>] [--overlay-repeat] [--force] [--password <pw>]", result.Output, StringComparison.Ordinal);
        Assert.Contains("--dry-run", result.Output, StringComparison.Ordinal);
        Assert.Contains("--fill <colour>", result.Output, StringComparison.Ordinal);
        Assert.Contains("--image-fill <how>", result.Output, StringComparison.Ordinal);
        Assert.Contains("--image-strength <s>", result.Output, StringComparison.Ordinal);
        Assert.Contains("NOT checked", result.Output, StringComparison.Ordinal);
        Assert.Contains("--rasterize-pages <range>", result.Output, StringComparison.Ordinal);
        Assert.Contains("--rasterize-unredactable", result.Output, StringComparison.Ordinal);
        Assert.Contains("cannot be checked for visible occurrences", result.Output, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ pages turned into images (milestone 14.1)

    /// <summary>The same bytes with another page size for every page that has the default box (same length, so the cross-reference table still fits).</summary>
    private string TwoPagesWithBox(string box)
    {
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(TwoPages()));
        var changed = text.Replace("/MediaBox [0 0 300 200] /Resources", $"/MediaBox{box}/Resources", StringComparison.Ordinal);
        Assert.NotEqual(text, changed);
        Assert.Equal(text.Length, changed.Length);
        var path = Path.Combine(WorkDirectory.FullName, "box.pdf");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(changed));
        return path;
    }

    /// <summary><c>redact-everywhere.pdf</c> with a first page of 6000 x 6000 pt: still the tiling pattern PDFium cannot redact around, but too large to become an image of 72 dpi or more.</summary>
    private string HugeUnredactablePage()
    {
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(Copy("redact-everywhere.pdf")));
        var changed = text.Replace("/MediaBox [0 0 300 400] /Annots", "/MediaBox[0 0 6000 6000]/Annots", StringComparison.Ordinal);
        Assert.NotEqual(text, changed);
        Assert.Equal(text.Length, changed.Length);
        var path = Path.Combine(WorkDirectory.FullName, "huge.pdf");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(changed));
        return path;
    }

    [Fact]
    public async Task RejectedFileWithoutRasterFlagExitsOne()
    {
        var input = Copy("redact-everywhere.pdf");
        var before = await File.ReadAllBytesAsync(input, Ct);

        var result = await RunAsync("redact", input, "--output", Output, "--text", Term, "--pages", "1");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Redacting page 1 would change content outside the marked areas, so the file is not redacted.", result.Error, StringComparison.Ordinal);

        // The way out is named, and it is a choice of the user, not a default.
        Assert.Contains("--rasterize-unredactable", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
        Assert.Empty(TemporaryFiles());
        Assert.Equal(before, await File.ReadAllBytesAsync(input, Ct));
    }

    [Fact]
    public async Task RasterizeUnredactableRescuesAndNamesThePages()
    {
        var input = Copy("redact-everywhere.pdf");
        var before = await File.ReadAllBytesAsync(input, Ct);

        // Document-wide, as the term must be gone from the whole file: page one becomes an image, the other pages are redacted in place.
        var result = await RunAsync("redact", input, "--output", Output, "--text", Term, "--rasterize-unredactable");

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Equal([Output], result.OutputLines);

        // The pages are named by number, with what they lose; never a value.
        Assert.Contains("--rasterize-unredactable turns page 1 into an image", result.Error, StringComparison.Ordinal);
        Assert.Contains("Pages turned into images:", result.Error, StringComparison.Ordinal);
        Assert.Contains("page 1: 200 dpi", result.Error, StringComparison.Ordinal);
        Assert.Contains("all of its text", result.Error, StringComparison.Ordinal);
        Assert.Contains("cannot be found without OCR", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Zephyr", result.Error, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(input, Ct));
        Assert.Empty(TemporaryFiles());

        await using var reopened = await Engine.OpenAsync(Output, cancellationToken: Ct);
        Assert.Equal(2, reopened.PageCount);
        Assert.Equal(string.Empty, (await reopened.GetTextAsync(0, Ct)).Trim());
        Assert.Equal(1, (await reopened.GetPageObjectSummaryAsync(0, Ct)).Images);
        Assert.Contains("Seite zwei", await reopened.GetTextAsync(1, Ct), StringComparison.Ordinal);
        Assert.False(RawPdf.ContainsAnywhere(await File.ReadAllBytesAsync(Output, Ct), Term));
    }

    [Fact]
    public async Task RasterizePagesNeedsNoMark()
    {
        var input = TwoPages();

        var result = await RunAsync("redact", input, "--rasterize-pages", "2", "--output", Output);

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Equal([Output], result.OutputLines);
        Assert.Contains("page 2: 200 dpi", result.Error, StringComparison.Ordinal);

        await using (var reopened = await Engine.OpenAsync(Output, cancellationToken: Ct))
        {
            Assert.Contains("public line", await reopened.GetTextAsync(0, Ct), StringComparison.Ordinal);
            Assert.Equal(string.Empty, (await reopened.GetTextAsync(1, Ct)).Trim());
            Assert.Equal(1, (await reopened.GetPageObjectSummaryAsync(1, Ct)).Images);
        }

        Assert.Empty(TemporaryFiles());

        // The resolution is the user's to choose.
        File.Delete(Output);
        var lower = await RunAsync("redact", input, "--rasterize-pages", "1-2", "--dpi", "100", "--output", Output);
        Assert.True(lower.ExitCode == 0, lower.Error);
        Assert.Contains("page 1: 100 dpi", lower.Error, StringComparison.Ordinal);
        Assert.Contains("page 2: 100 dpi", lower.Error, StringComparison.Ordinal);

        // A page beyond the end is an error like for --pages.
        var beyondTarget = Path.Combine(WorkDirectory.FullName, "beyond.pdf");
        var beyond = await RunAsync("redact", input, "--rasterize-pages", "3", "--output", beyondTarget);
        Assert.Equal(1, beyond.ExitCode);
        Assert.Contains("Page 3 is out of range", beyond.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(beyondTarget));
    }

    [Theory]
    [InlineData("71")]
    [InlineData("301")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("200.5")]
    public async Task DpiOutsideTheRangeIsUsageError(string dpi)
    {
        var result = await RunAsync("redact", TwoPages(), "--rasterize-pages", "1", "--dpi", dpi, "--output", Output);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--dpi", result.Error, StringComparison.Ordinal);
        Assert.Contains("72", result.Error, StringComparison.Ordinal);
        Assert.Contains("Usage:", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public async Task RasterOptionsWithoutTheirPartnerAreUsageErrors()
    {
        var input = TwoPages();

        // --dpi only sets the resolution of pages that become images.
        var dpiAlone = await RunAsync("redact", input, "--text", "secret value", "--dpi", "150", "--output", Output);
        Assert.Equal(2, dpiAlone.ExitCode);
        Assert.Contains("--dpi", dpiAlone.Error, StringComparison.Ordinal);
        Assert.Contains("--rasterize-pages", dpiAlone.Error, StringComparison.Ordinal);

        // --rasterize-unredactable only rescues marked pages: without a mark there is nothing to rescue.
        var rescueAlone = await RunAsync("redact", input, "--rasterize-unredactable", "--output", Output);
        Assert.Equal(2, rescueAlone.ExitCode);
        Assert.Contains("--rasterize-unredactable", rescueAlone.Error, StringComparison.Ordinal);

        var badRange = await RunAsync("redact", input, "--rasterize-pages", "5-3", "--output", Output);
        Assert.Equal(2, badRange.ExitCode);
        Assert.Contains("--rasterize-pages", badRange.Error, StringComparison.Ordinal);

        var twice = await RunAsync("redact", input, "--rasterize-pages", "1", "--rasterize-pages", "2", "--output", Output);
        Assert.Equal(2, twice.ExitCode);
        Assert.Contains("given twice", twice.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public async Task DryRunPredictsRasterPages()
    {
        var input = TwoPages();
        var before = await File.ReadAllBytesAsync(input, Ct);

        var result = await RunAsync("redact", input, "--rasterize-pages", "2", "--dpi", "150", "--dry-run");

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Empty(result.OutputBytes);
        Assert.False(File.Exists(Output));
        Assert.Empty(TemporaryFiles());
        Assert.Equal(before, await File.ReadAllBytesAsync(input, Ct));
        Assert.Contains("Dry run: nothing is written.", result.Error, StringComparison.Ordinal);
        Assert.Contains("Pages turned into images:", result.Error, StringComparison.Ordinal);
        Assert.Contains("page 2: 150 dpi (625 x 417 pixels", result.Error, StringComparison.Ordinal);
        Assert.Contains("all of its text", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("page 1: ", result.Error, StringComparison.Ordinal);

        // A page that is too large for the resolution is told, never lowered in silence.
        var large = await RunAsync("redact", TwoPagesWithBox("[0 0 3000 3000]"), "--rasterize-pages", "1", "--dry-run");
        Assert.True(large.ExitCode == 0, large.Error);
        Assert.Matches(@"page 1: \d+ dpi, lowered from 200 dpi because the page is large", large.Error);

        // The rescue is predicted too.
        var rescue = await RunAsync("redact", Copy("redact-everywhere.pdf"), "--text", Term, "--pages", "1", "--rasterize-unredactable", "--dry-run");
        Assert.True(rescue.ExitCode == 0, rescue.Error);
        Assert.Contains("--rasterize-unredactable turns page 1 into an image", rescue.Error, StringComparison.Ordinal);
        Assert.Contains("page 1: 200 dpi", rescue.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public async Task RasterizeFlagDoesNotRescueEncryptedOrPageKeyFiles()
    {
        // Encrypted: refused before anything else, the flag changes nothing.
        var encrypted = Copy("encrypted_hello_world_r3.pdf");
        var env = new Dictionary<string, string> { ["PAGEWING_PASSWORD"] = UserPassword };
        var locked = await RunAsync(env, "redact", encrypted, "--output", Output, "--rect", "1:10,10,50,20", "--rasterize-unredactable");
        Assert.Equal(1, locked.ExitCode);
        Assert.Contains("pagewing: The file is encrypted.", locked.Error, StringComparison.Ordinal);
        var lockedPages = await RunAsync(env, "redact", encrypted, "--output", Output, "--rasterize-pages", "1");
        Assert.Equal(1, lockedPages.ExitCode);
        Assert.Contains("pagewing: The file is encrypted.", lockedPages.Error, StringComparison.Ordinal);

        // A page key PDFium cannot remove: the same message as without the flag, and the page is named.
        var file = new VerifierPdf("BT /F1 12 Tf 20 150 Td (one) Tj ET", "BT /F1 12 Tf 20 150 Td (two) Tj ET");
        var stream = file.AddStream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray", [0x66]);
        file.PageEntriesByPage = ["", $"/Thumb {stream} 0 R"];
        var thumb = Path.Combine(WorkDirectory.FullName, "thumb.pdf");
        await File.WriteAllBytesAsync(thumb, file.Build(), Ct);
        var without = await RunAsync("redact", thumb, "--output", Output, "--rect", "1:15,35,45,25");
        var with = await RunAsync("redact", thumb, "--output", Output, "--rect", "1:15,35,45,25", "--rasterize-unredactable");
        var onThePage = await RunAsync("redact", thumb, "--output", Output, "--rasterize-pages", "2");

        Assert.Equal(1, without.ExitCode);
        Assert.Equal(1, with.ExitCode);
        Assert.Equal(without.Error, with.Error);
        Assert.Equal(1, onThePage.ExitCode);
        Assert.Contains("are on page 2, so the file is not redacted.", onThePage.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task APageThatCannotBecomeAnImageStaysARefusal()
    {
        // Too large for 72 dpi: the choice does not rescue it, the command does not loop, and the page is named.
        var chosen = await RunAsync("redact", TwoPagesWithBox("[0 0 6000 6000]"), "--rasterize-pages", "2", "--output", Output);
        Assert.Equal(1, chosen.ExitCode);
        Assert.Contains("page 2", chosen.Error, StringComparison.Ordinal);

        // The page was chosen already, so the way out of the other refusal is not offered for it.
        Assert.DoesNotContain("--rasterize-unredactable", chosen.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));

        var input = HugeUnredactablePage();
        var before = await File.ReadAllBytesAsync(input, Ct);
        var rescue = await RunAsync("redact", input, "--output", Output, "--text", Term, "--pages", "1", "--rasterize-unredactable");

        Assert.Equal(1, rescue.ExitCode);
        Assert.Contains("page 1", rescue.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Zephyr", rescue.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
        Assert.Empty(TemporaryFiles());
        Assert.Equal(before, await File.ReadAllBytesAsync(input, Ct));
    }

    // ------------------------------------------------------------------ patterns (milestone 14.1)

    private const string ValidIban = "DE89 3704 0044 0532 0130 00";

    private const string TypoIban = "DE88 3704 0044 0532 0130 00";

    // Every value of the fixture that a report must never repeat: the groups of the IBANs and the cards, valid or not.
    private static readonly string[] ValueFragments = ["3704", "0044", "0532", "0130", "ABNA", "0417", "1234", "5678", "9012", "3456", "4111", "5555", "WEST"];

    /// <summary>The text of every page without spaces and tabs (a group separator may be a space or a no-break space), to look for a number as one run; line breaks stay, so a number split over two lines is not one run.</summary>
    private async Task<string> CompactTextAsync(string path) =>
        string.Concat((await PageTextsAsync(path)).SelectMany(static t => t).Where(static c => c is not (' ' or ' ' or '\t')));

    private string PatternPages(string first, string second)
    {
        var pdf = new VerifierPdf($"BT /F1 12 Tf 20 150 Td ({first}) Tj ET BT /F1 12 Tf 20 100 Td (public line) Tj ET", $"BT /F1 12 Tf 20 150 Td ({second}) Tj ET BT /F1 12 Tf 20 100 Td (public line) Tj ET");
        var path = Path.Combine(WorkDirectory.FullName, "patterns.pdf");
        File.WriteAllBytes(path, pdf.Build());
        return path;
    }

    [Fact]
    public async Task PatternIbanIsRedactedAndVerified()
    {
        var input = Copy("redact-patterns.pdf");
        var before = await File.ReadAllBytesAsync(input, Ct);
        var original = await CompactTextAsync(input);
        Assert.Contains("DE89370400440532013000", original, StringComparison.Ordinal);

        var result = await RunAsync("redact", input, "--output", Output, "--pattern", "iban");

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Equal([Output], result.OutputLines);
        Assert.Equal(before, await File.ReadAllBytesAsync(input, Ct));
        Assert.Empty(TemporaryFiles());
        Assert.Contains("Removed:", result.Error, StringComparison.Ordinal);
        Assert.Contains("Checked:", result.Error, StringComparison.Ordinal);

        // The IBANs with a right checksum are gone from the text of the new file, in every spelling, and from its bytes.
        var text = await CompactTextAsync(Output);
        var bytes = await File.ReadAllBytesAsync(Output, Ct);
        foreach (var iban in new[] { "DE89370400440532013000", "GB82WEST12345698765432", "FR1420041010050500013M02606", "NL91ABNA0417164300" })
        {
            Assert.DoesNotContain(iban, text, StringComparison.Ordinal);
            Assert.False(RawPdf.ContainsAnywhere(bytes, iban), iban);
        }

        Assert.False(RawPdf.ContainsAnywhere(bytes, ValidIban));
        Assert.False(RawPdf.ContainsAnywhere(bytes, "GB82 WEST 1234 5698 7654 32"));

        // The note of the fixture held an IBAN too (the note is removed with the mark).
        Assert.False(RawPdf.ContainsAnywhere(bytes, "Please transfer"));

        // Everything else is still there: the IBAN with the wrong check digit (not redacted without the switch), cards, telephone numbers, mail.
        Assert.Contains("(typo)DE88370400440532013000", text, StringComparison.Ordinal);
        Assert.Contains("4111111111111111", text, StringComparison.Ordinal);
        Assert.Contains("+497111234567", text, StringComparison.Ordinal);
        Assert.Contains("max.mustermann@example.com", text, StringComparison.Ordinal);
        Assert.Contains("01.02.2024", text, StringComparison.Ordinal);

        // The report names what is not searched, never a value.
        Assert.Contains("Not searched:", result.Error, StringComparison.Ordinal);
        foreach (var line in Pagewing.Core.Redaction.RedactionSearch.NotSearched)
        {
            Assert.Contains(line, result.Error, StringComparison.Ordinal);
        }

        foreach (var fragment in ValueFragments)
        {
            Assert.DoesNotContain(fragment, result.Error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ChecksumFailedHitsAreReportedWithoutValuesAndNotRedacted()
    {
        var input = Copy("redact-patterns.pdf");

        var result = await RunAsync("redact", input, "--output", Output, "--pattern", "iban", "--pattern", "card");

        Assert.True(result.ExitCode == 0, result.Error);

        // The count and the page of what was skipped, kind by kind, and the way to include it.
        Assert.Contains("Skipped (not redacted): 2 pattern matches whose checksum or length is wrong (iban: 1, card: 1) on page 1.", result.Error, StringComparison.Ordinal);
        Assert.Contains("--include-unchecked", result.Error, StringComparison.Ordinal);
        foreach (var fragment in ValueFragments)
        {
            Assert.DoesNotContain(fragment, result.Error, StringComparison.Ordinal);
        }

        var text = await CompactTextAsync(Output);
        Assert.Contains("(typo)DE88370400440532013000", text, StringComparison.Ordinal);
        Assert.Contains("(typo)1234567890123456", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DE89370400440532013000", text, StringComparison.Ordinal);
        Assert.DoesNotContain("4111111111111111", text, StringComparison.Ordinal);
        Assert.DoesNotContain("5555555555554444", text, StringComparison.Ordinal);
        var bytes = await File.ReadAllBytesAsync(Output, Ct);
        Assert.False(RawPdf.ContainsAnywhere(bytes, "4111 1111 1111 1111"));
        Assert.False(RawPdf.ContainsAnywhere(bytes, "4111-1111-1111-1111"));
    }

    [Fact]
    public async Task IncludeUncheckedRedactsThem()
    {
        var input = Copy("redact-patterns.pdf");

        var result = await RunAsync("redact", input, "--output", Output, "--pattern", "iban", "--pattern", "card", "--include-unchecked");

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.DoesNotContain("Skipped", result.Error, StringComparison.Ordinal);
        foreach (var fragment in ValueFragments)
        {
            Assert.DoesNotContain(fragment, result.Error, StringComparison.Ordinal);
        }

        var text = await CompactTextAsync(Output);
        var bytes = await File.ReadAllBytesAsync(Output, Ct);
        Assert.DoesNotContain("(typo)DE88370400440532013000", text, StringComparison.Ordinal);
        Assert.DoesNotContain("(typo)1234567890123456", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DE89370400440532013000", text, StringComparison.Ordinal);
        Assert.False(RawPdf.ContainsAnywhere(bytes, TypoIban));
        Assert.False(RawPdf.ContainsAnywhere(bytes, "1234 5678 9012 3456"));
        Assert.False(RawPdf.ContainsAnywhere(bytes, ValidIban));

        // Kinds that were not asked for stay.
        Assert.Contains("+497111234567", text, StringComparison.Ordinal);
        Assert.Contains("max.mustermann@example.com", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PatternWithoutAnyHitExitsOneLikeTextWithoutHit()
    {
        var input = PatternPages("secret value", "public line");

        var none = await RunAsync("redact", input, "--output", Output, "--pattern", "email");

        Assert.Equal(1, none.ExitCode);
        Assert.Contains("pagewing: No match for the patterns (email).", none.Error, StringComparison.Ordinal);
        Assert.Contains("Nothing was written.", none.Error, StringComparison.Ordinal);
        Assert.Contains("Not searched:", none.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
        Assert.Empty(TemporaryFiles());

        // Also as a dry run, and with a text that is found: the patterns asked for found nothing.
        var dry = await RunAsync("redact", input, "--text", "secret value", "--pattern", "iban", "--pattern", "card", "--dry-run");
        Assert.Equal(1, dry.ExitCode);
        Assert.Contains("No match for the patterns (iban, card).", dry.Error, StringComparison.Ordinal);

        // Only matches with a wrong checksum: nothing is redacted, and the message says why and how to include them.
        var typo = PatternPages(TypoIban, "public line");
        var skipped = await RunAsync("redact", typo, "--output", Output, "--pattern", "iban");
        Assert.Equal(1, skipped.ExitCode);
        Assert.Contains("Skipped (not redacted): 1 pattern match whose checksum or length is wrong (iban: 1) on page 1.", skipped.Error, StringComparison.Ordinal);
        Assert.Contains("--include-unchecked", skipped.Error, StringComparison.Ordinal);
        Assert.Contains("Nothing was written.", skipped.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("3704", skipped.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
    }

    [Theory]
    [InlineData("--pattern", "ibann")]
    [InlineData("--pattern", "")]
    [InlineData("--pattern", "IBAN%")]
    public async Task UnknownPatternIsUsageError(string option, string value)
    {
        var result = await RunAsync("redact", PatternPages(ValidIban, "x"), "--output", Output, option, value);

        Assert.Equal(2, result.ExitCode);
        Assert.StartsWith("pagewing: ", result.Error, StringComparison.Ordinal);
        Assert.Contains("--pattern", result.Error, StringComparison.Ordinal);
        Assert.Contains("email, iban, phone, card", result.Error, StringComparison.Ordinal);
        Assert.Contains("Usage:", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public async Task IncludeUncheckedWithoutPatternIsUsageError()
    {
        var result = await RunAsync("redact", TwoPages(), "--output", Output, "--text", "secret value", "--include-unchecked");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--include-unchecked", result.Error, StringComparison.Ordinal);
        Assert.Contains("--pattern", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));

        // The switch is for redact only, and --pattern alone is a complete request (it is something to redact).
        var other = await RunAsync("text", TwoPages(), "--pattern", "iban");
        Assert.Equal(2, other.ExitCode);
        Assert.Contains("Unknown option '--pattern' for 'text'", other.Error, StringComparison.Ordinal);
        var twice = await RunAsync("redact", PatternPages(ValidIban, "x"), "--output", Output, "--pattern", "iban", "--include-unchecked", "--include-unchecked");
        Assert.Equal(2, twice.ExitCode);
        Assert.Contains("given twice", twice.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PatternRespectsPages()
    {
        var input = PatternPages($"IBAN {ValidIban}", $"IBAN {ValidIban}");

        var result = await RunAsync("redact", input, "--output", Output, "--pattern", "iban", "--pages", "2");

        Assert.True(result.ExitCode == 0, result.Error);
        var pages = await PageTextsAsync(Output);
        Assert.Contains("DE89 3704 0044 0532 0130 00", pages[0], StringComparison.Ordinal);
        Assert.DoesNotContain("3704", pages[1], StringComparison.Ordinal);
        Assert.Contains("public line", pages[1], StringComparison.Ordinal);

        // A page without a match is "no match", also when the other page has one.
        var onlyFirst = PatternPages($"IBAN {ValidIban}", "nothing here");
        var none = await RunAsync("redact", onlyFirst, "--output", Path.Combine(WorkDirectory.FullName, "none.pdf"), "--pattern", "iban", "--pages", "2");
        Assert.Equal(1, none.ExitCode);
        Assert.Contains("No match for the patterns (iban) on page 2.", none.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PatternsCombineWithTextAndRectangles()
    {
        var input = PatternPages($"IBAN {ValidIban} secret value", "secret value");

        var result = await RunAsync("redact", input, "--output", Output, "--pattern", "iban", "--text", "secret value", "--rect", "2:15,90,100,16");

        Assert.True(result.ExitCode == 0, result.Error);
        var text = await CompactTextAsync(Output);
        Assert.DoesNotContain("3704", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secretvalue", text, StringComparison.Ordinal);
        Assert.False(RawPdf.ContainsAnywhere(await File.ReadAllBytesAsync(Output, Ct), "secret value"));
        Assert.Contains("IBAN", text, StringComparison.Ordinal);

        // The rectangle took "public line" from page 2 only.
        var pages = await PageTextsAsync(Output);
        Assert.Contains("public line", pages[0], StringComparison.Ordinal);
        Assert.DoesNotContain("public line", pages[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task PatternDryRunCountsPerKindAndCertaintyWithoutValues()
    {
        var input = Copy("redact-patterns.pdf");
        var before = await File.ReadAllBytesAsync(input, Ct);

        var result = await RunAsync("redact", input, "--pattern", "iban", "--pattern", "card", "--pattern", "phone", "--pattern", "email", "--dry-run");

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Empty(result.OutputBytes);
        Assert.False(File.Exists(Output));
        Assert.Empty(TemporaryFiles());
        Assert.Equal(before, await File.ReadAllBytesAsync(input, Ct));
        Assert.Contains("Dry run: nothing is written.", result.Error, StringComparison.Ordinal);
        Assert.Contains("Pattern iban: 7 matches (6 checksum ok, 1 checksum wrong - not redacted without --include-unchecked)", result.Error, StringComparison.Ordinal);
        Assert.Contains("Pattern card: 4 matches (3 checksum ok, 1 checksum wrong - not redacted without --include-unchecked)", result.Error, StringComparison.Ordinal);
        Assert.Contains("Pattern phone: 4 matches (4 shape only, no checksum)", result.Error, StringComparison.Ordinal);
        Assert.Contains("Pattern email: 3 matches (3 shape only, no checksum)", result.Error, StringComparison.Ordinal);
        Assert.Contains("  page 1: 7", result.Error, StringComparison.Ordinal);
        Assert.Contains("Skipped (not redacted): 2 pattern matches whose checksum or length is wrong", result.Error, StringComparison.Ordinal);
        Assert.Contains("Not searched:", result.Error, StringComparison.Ordinal);
        foreach (var fragment in ValueFragments.Concat(["example", "mustermann", "+49", "0711"]))
        {
            Assert.DoesNotContain(fragment, result.Error, StringComparison.Ordinal);
        }

        // With the switch the checksum failures are counted as redacted and nothing is skipped.
        var included = await RunAsync("redact", input, "--pattern", "iban", "--include-unchecked", "--dry-run");
        Assert.True(included.ExitCode == 0, included.Error);
        Assert.Contains("Pattern iban: 7 matches (6 checksum ok, 1 checksum wrong - redacted with --include-unchecked)", included.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Skipped", included.Error, StringComparison.Ordinal);
    }

    /// <summary>Page one holds an IBAN; page two has a note without a /Rect, which the search cannot read (reported as not searched).</summary>
    private string UnsearchedPatternPage()
    {
        var pdf = new VerifierPdf($"BT /F1 12 Tf 20 150 Td ({ValidIban}) Tj ET", "BT /F1 12 Tf 20 150 Td (Plain) Tj ET");
        var damaged = pdf.Add($"<< /Type /Annot /Subtype /Text /Contents {Utf16("Note without a rect")} /F 4 >>");
        pdf.PageEntriesByPage = [string.Empty, $"/Annots [{damaged} 0 R]"];
        var path = Path.Combine(WorkDirectory.FullName, "unsearched-patterns.pdf");
        File.WriteAllBytes(path, pdf.Build());
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APatternRunOnAPageNotSearchedCompletelyStopsLikeText(bool dryRun)
    {
        var input = UnsearchedPatternPage();
        var args = new List<string> { "redact", input, "--output", Output, "--pattern", "iban" };
        if (dryRun)
        {
            args.Add("--dry-run");
        }

        var stopped = await RunAsync([.. args]);

        Assert.Equal(1, stopped.ExitCode);
        Assert.Contains("pagewing: Page 2 could not be searched completely, so the text may still be there. Nothing was written.", stopped.Error, StringComparison.Ordinal);
        Assert.Contains("--allow-unsearched", stopped.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));

        // Accepted, it goes on and says so; --pages 1 does not need the page that cannot be read.
        args.Add("--allow-unsearched");
        var accepted = await RunAsync([.. args]);
        Assert.True(accepted.ExitCode == 0, accepted.Error);
        Assert.Contains("Not searched (accepted):", accepted.Error, StringComparison.Ordinal);
        Assert.Contains("- page 2 could not be searched completely", accepted.Error, StringComparison.Ordinal);
        Assert.Equal(!dryRun, File.Exists(Output));

        if (!dryRun)
        {
            Assert.DoesNotContain("3704", await CompactTextAsync(Output), StringComparison.Ordinal);
        }

        var limited = await RunAsync("redact", input, "--output", Path.Combine(WorkDirectory.FullName, "page-one.pdf"), "--pattern", "iban", "--pages", "1", "--dry-run");
        Assert.Equal(0, limited.ExitCode);
    }

    [Fact]
    public async Task AReachedSearchLimitStopsAPatternRun()
    {
        // 200 pages with 60 addresses each are 12,000 places; the search stops at 10,000 and they cannot be removed everywhere.
        var content = string.Join(' ', Enumerable.Repeat("a@b.cd", 60));
        var pdf = new VerifierPdf([.. Enumerable.Range(0, 200).Select(_ => $"BT /F1 1 Tf 5 190 Td ({content}) Tj ET")]);
        var input = Path.Combine(WorkDirectory.FullName, "many-mails.pdf");
        await File.WriteAllBytesAsync(input, pdf.Build(), Ct);

        var result = await RunAsync("redact", input, "--output", Output, "--pattern", "email", "--dry-run");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("pagewing: The search stopped at 10000 matches and more may exist", result.Error, StringComparison.Ordinal);
        Assert.Contains("Nothing was written.", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public async Task HelpListsThePatternOptions()
    {
        var result = await RunAsync("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("[--pattern <email|iban|phone|card>]...", result.Output, StringComparison.Ordinal);
        Assert.Contains("[--include-unchecked]", result.Output, StringComparison.Ordinal);
        Assert.Contains("--include-unchecked", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("all IBANs", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    // ---- --image-fill and --image-strength (milestone 14.1, task 13) ----
    // redact-image-blocks.pdf is 330 pt high; --rect is page:x,y,width,height from the top left, so 1:46,56,72,72 is the area over the
    // first image (columns 24 to 72 and rows 20 to 68 of its 96 x 96 pixels).

    private static readonly PageRect ImageArea = new(46, 56, 118, 128);

    /// <summary>The part of the first image of page 1 that lies under <see cref="ImageArea"/> in a file (blue, green, red, alpha), with its size.</summary>
    private async Task<(int Width, int Height, byte[] Bgra)> ImageUnderTheAreaAsync(string path)
    {
        await using var document = await Engine.OpenAsync(path, cancellationToken: Ct);
        var part = Assert.Single(await document.GetImageAreaPixelsAsync(0, ImageArea, Ct));
        return (part.Width, part.Height, part.Bgra);
    }

    [Theory]
    [InlineData("pixelate", "coarse", RedactionImageTreatment.Pixelate, RedactionImageStrength.Coarse)]
    [InlineData("pixelate", "very-coarse", RedactionImageTreatment.Pixelate, RedactionImageStrength.VeryCoarse)]
    [InlineData("blur", null, RedactionImageTreatment.Blur, RedactionImageStrength.Coarse)]
    [InlineData("BLUR", "VERY-COARSE", RedactionImageTreatment.Blur, RedactionImageStrength.VeryCoarse)]
    public async Task CliImageFillPixelatesAndVerifies(string fill, string? strength, RedactionImageTreatment treatment, RedactionImageStrength expectedStrength)
    {
        var input = Copy("redact-image-blocks.pdf");
        var before = await ImageUnderTheAreaAsync(input);
        string[] arguments = ["redact", input, "--output", Output, "--rect", "1:46,56,72,72", "--image-fill", fill];
        if (strength is not null)
        {
            arguments = [.. arguments, "--image-strength", strength];
        }

        var result = await RunAsync(arguments);

        // The command ran its own check of the written file (a failing check is exit 1 and no file), and what it wrote is read back here.
        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Equal([Output], result.OutputLines);
        var after = await ImageUnderTheAreaAsync(Output);
        Assert.Equal((before.Width, before.Height), (after.Width, after.Height));
        var grid = ImageBlocks.Plan(before.Width, before.Height, expectedStrength)!;
        var expected = ImageBlocks.Expected(before.Bgra, before.Width, before.Height, new PixelRect(0, 0, before.Width, before.Height), grid, treatment);
        Assert.NotEqual(before.Bgra, expected);
        Assert.Equal(expected, after.Bgra);

        // The report on stderr counts the areas and says what the check cannot say; it never carries a pixel or a removed text.
        Assert.Contains("Image areas:", result.Error, StringComparison.Ordinal);
        Assert.Contains(treatment == RedactionImageTreatment.Pixelate ? "1 image area pixelated" : "1 image area blurred", result.Error, StringComparison.Ordinal);
        Assert.Contains(RedactionReportText.ImageTextCaution, result.Error, StringComparison.Ordinal);
        Assert.Contains("does not recognise text in images", result.Error, StringComparison.Ordinal);
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task CliImageFillDefaultsToSolid()
    {
        var input = Copy("redact-image-blocks.pdf");
        foreach (var extra in new[] { Array.Empty<string>(), ["--image-fill", "solid"] })
        {
            var output = Path.Combine(WorkDirectory.FullName, $"out-{extra.Length}.pdf");
            var result = await RunAsync(["redact", input, "--output", output, "--rect", "1:46,56,72,72", .. extra]);
            Assert.True(result.ExitCode == 0, result.Error);
            var after = await ImageUnderTheAreaAsync(output);
            for (var i = 0; i < after.Bgra.Length; i += 4)
            {
                Assert.Equal([0, 0, 0, 255], after.Bgra[i..(i + 4)]);
            }

            // Nothing is said about treated image areas when none were asked for.
            Assert.DoesNotContain("Image areas:", result.Error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task CliImageOptionsAreUsageErrorsWhenWrong()
    {
        var input = Copy("redact-image-blocks.pdf");
        string[] baseline = ["redact", input, "--output", Output, "--rect", "1:46,56,72,72"];

        // An unknown value, a value that is missing, an option twice, and a strength without blocks to be coarse: exit 2, nothing written.
        foreach (var bad in new[]
        {
            new[] { "--image-fill", "sharp" },
            new[] { "--image-fill", string.Empty },
            new[] { "--image-fill" },
            new[] { "--image-fill", "blur", "--image-strength", "fine" },
            new[] { "--image-fill", "blur", "--image-strength", "coarse", "--image-strength", "very-coarse" },
            new[] { "--image-fill", "blur", "--image-fill", "pixelate" },
            new[] { "--image-strength", "coarse" },
            new[] { "--image-fill", "solid", "--image-strength", "very-coarse" },
        })
        {
            var result = await RunAsync([.. baseline, .. bad]);
            Assert.Equal(2, result.ExitCode);
            Assert.Contains("--image-", result.Error, StringComparison.Ordinal);
            Assert.False(File.Exists(Output));
        }
    }

    [Fact]
    public async Task CliDryRunCountsTheTreatedAndTheSolidAreas()
    {
        // The area over the first image is treated; the one over the 7 x 7 pixel image (3 x 3 pixels under it) gets the solid fill.
        var input = Copy("redact-image-blocks.pdf");
        var result = await RunAsync("redact", input, "--dry-run", "--rect", "1:46,56,72,72", "--rect", "1:20,210,20,20", "--image-fill", "blur", "--image-strength", "very-coarse");

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.False(File.Exists(Output));
        Assert.Contains("1 image area will be blurred", result.Error, StringComparison.Ordinal);
        Assert.Contains("1 image area gets the solid fill instead", result.Error, StringComparison.Ordinal);
        Assert.Contains(RedactionReportText.ImageSolidReasonText(ImageSolidReason.TooSmall), result.Error, StringComparison.Ordinal);
        Assert.Contains(RedactionReportText.ImageTextCaution, result.Error, StringComparison.Ordinal);
    }

    // ---- --overlay-text and --overlay-repeat (milestone 14.1, task 16) ----
    // TwoPages: 300 x 200 pt, "secret value" at (20, 150) on both pages, which is the band 15,38 to 110,56 from the top left.

    /// <summary>
    /// Two pages with "secret value" in 24 pt (the box of a text mark is then high enough for the smallest overlay text of 6 pt; a mark on small
    /// body text is not, and the report counts it as not fitting) and "public line" in 12 pt.
    /// </summary>
    private string LargeTextPages()
    {
        const string Content = "BT /F1 24 Tf 20 150 Td (secret value) Tj ET BT /F1 12 Tf 20 100 Td (public line) Tj ET";
        var path = Path.Combine(WorkDirectory.FullName, "large.pdf");
        File.WriteAllBytes(path, new VerifierPdf(Content, Content).Build());
        return path;
    }

    /// <summary>The text objects and the bounds of the path objects of a page of a written file.</summary>
    private async Task<(int Texts, IReadOnlyList<PageRect> Paths)> PageObjectsAsync(string path, int page)
    {
        await using var document = await Engine.OpenAsync(path, cancellationToken: Ct);
        var summary = await document.GetPageObjectSummaryAsync(page, Ct);
        return (summary.Texts, summary.PathBounds ?? []);
    }

    [Fact]
    public async Task CliOverlayTextAppliesToAllMarksAndRefusesATerm()
    {
        var input = LargeTextPages();

        // Without the option the areas are plain fills: the baseline of the path objects.
        var plain = Path.Combine(WorkDirectory.FullName, "plain.pdf");
        Assert.Equal(0, (await RunAsync("redact", input, "--output", plain, "--text", "secret value")).ExitCode);
        var plainPage1 = await PageObjectsAsync(plain, 0);
        var plainPage2 = await PageObjectsAsync(plain, 1);

        // The text goes on every mark of the call (both pages, found by --text), is drawn as path objects, and leaves no text object behind.
        var result = await RunAsync("redact", input, "--output", Output, "--text", "secret value", "--overlay-text", "redacted");
        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Equal([Output], result.OutputLines);
        var page1 = await PageObjectsAsync(Output, 0);
        var page2 = await PageObjectsAsync(Output, 1);
        Assert.Equal(plainPage1.Texts, page1.Texts);
        Assert.Equal(plainPage2.Texts, page2.Texts);
        Assert.True(page1.Paths.Count > plainPage1.Paths.Count, "page 1 got no overlay paths");
        Assert.True(page2.Paths.Count > plainPage2.Paths.Count, "page 2 got no overlay paths");

        // Nothing reaches outside the filled area.
        var fill = plainPage1.Paths.Aggregate(static (a, b) => new PageRect(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom)));
        Assert.All(page1.Paths, bounds => Assert.True(
            bounds.Left >= fill.Left - RedactionVerifier.PathSlack && bounds.Right <= fill.Right + RedactionVerifier.PathSlack
            && bounds.Top >= fill.Top - RedactionVerifier.PathSlack && bounds.Bottom <= fill.Bottom + RedactionVerifier.PathSlack,
            "a path lies outside the filled area"));
        var bytes = await File.ReadAllBytesAsync(Output, Ct);
        Assert.False(RawPdf.ContainsAnywhere(bytes, "redacted"));
        Assert.False(RawPdf.ContainsAnywhere(bytes, "secret value"));
        Assert.Empty(TemporaryFiles());

        // The report says honestly what the check does not read.
        Assert.Contains("not read back", result.Error, StringComparison.Ordinal);

        // A rectangle is a mark of the call too.
        var rect = Path.Combine(WorkDirectory.FullName, "rect.pdf");
        var viaRect = await RunAsync("redact", input, "--output", rect, "--rect", "1:15,38,95,18", "--overlay-text", "redacted");
        Assert.True(viaRect.ExitCode == 0, viaRect.Error);
        Assert.True((await PageObjectsAsync(rect, 0)).Paths.Count > plainPage1.Paths.Count);

        // A text that holds a redacted term, whatever its case or spacing, ends the command: exit 1, nothing written, the term not repeated.
        foreach (var dry in new[] { false, true })
        {
            var refused = Path.Combine(WorkDirectory.FullName, $"refused-{dry}.pdf");
            string[] arguments = dry
                ? ["redact", input, "--text", "secret value", "--overlay-text", "see SECRET   value here", "--dry-run"]
                : ["redact", input, "--text", "secret value", "--overlay-text", "see SECRET   value here", "--output", refused];
            var no = await RunAsync(arguments);
            Assert.Equal(1, no.ExitCode);
            Assert.Contains("contains a text that is redacted", no.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", no.Error, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(refused));
        }

        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task CliOverlayRepeat()
    {
        var input = TwoPages();
        string[] marks = ["--rect", "1:15,38,200,60"];

        // Repeating needs the text it repeats.
        var alone = await RunAsync(["redact", input, "--output", Output, .. marks, "--overlay-repeat"]);
        Assert.Equal(2, alone.ExitCode);
        Assert.Contains("--overlay-repeat", alone.Error, StringComparison.Ordinal);
        Assert.Contains("--overlay-text", alone.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));

        // One run draws the text once, the other tiles it over the area.
        var once = Path.Combine(WorkDirectory.FullName, "once.pdf");
        var tiled = Path.Combine(WorkDirectory.FullName, "tiled.pdf");
        Assert.Equal(0, (await RunAsync(["redact", input, "--output", once, .. marks, "--overlay-text", "ab cd"])).ExitCode);
        var repeat = await RunAsync(["redact", input, "--output", tiled, .. marks, "--overlay-text", "ab cd", "--overlay-repeat"]);
        Assert.True(repeat.ExitCode == 0, repeat.Error);
        var onceCount = (await PageObjectsAsync(once, 0)).Paths.Count;
        var tiledCount = (await PageObjectsAsync(tiled, 0)).Paths.Count;
        Assert.True(tiledCount > onceCount, $"{tiledCount} paths tiled against {onceCount} once");
    }

    [Theory]
    [InlineData("--overlay-text", "")]
    [InlineData("--overlay-text", "   ")]
    public async Task CliOverlayTextThatIsEmptyIsAUsageError(string option, string value)
    {
        var result = await RunAsync("redact", TwoPages(), "--output", Output, "--text", "secret value", option, value);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--overlay-text", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public async Task CliOverlayTextWithTooManyOrUndrawableCharactersIsAUsageError()
    {
        var input = TwoPages();
        string[] baseline = ["redact", input, "--output", Output, "--text", "secret value"];

        // 201 characters: one too many.
        var tooLong = await RunAsync([.. baseline, "--overlay-text", new string('a', RedactionLimits.MaxOverlayTextLength + 1)]);
        Assert.Equal(2, tooLong.ExitCode);
        Assert.Contains("200", tooLong.Error, StringComparison.Ordinal);

        // 200 is fine to parse (the engine then decides whether it fits).
        Assert.Equal(0, (await RunAsync([.. baseline, "--overlay-text", new string('a', RedactionLimits.MaxOverlayTextLength)])).ExitCode);
        File.Delete(Output);

        // A character the font has not: the text is refused as a whole and the message names the position, never the text.
        var bad = await RunAsync([.. baseline, "--overlay-text", "ok 日 text"]);
        Assert.Equal(2, bad.ExitCode);
        Assert.Contains("position 4", bad.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("日", bad.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));

        // Given twice, or without any mark to put it on.
        Assert.Equal(2, (await RunAsync([.. baseline, "--overlay-text", "a", "--overlay-text", "b"])).ExitCode);
        var noMark = await RunAsync("redact", input, "--output", Output, "--rasterize-pages", "1", "--overlay-text", "x");
        Assert.Equal(2, noMark.ExitCode);
        Assert.Contains("--overlay-text", noMark.Error, StringComparison.Ordinal);
        Assert.Equal(2, (await RunAsync([.. baseline, "--overlay-text"])).ExitCode);
    }

    [Fact]
    public async Task CliDryRunCountsOverlayTextWithoutValues()
    {
        var input = LargeTextPages();

        var fits = await RunAsync("redact", input, "--dry-run", "--text", "secret value", "--overlay-text", "zqmarker");
        Assert.True(fits.ExitCode == 0, fits.Error);
        Assert.True(fits.Error.Contains("Overlay text: 2 areas get the overlay text", StringComparison.Ordinal), fits.Error);
        Assert.DoesNotContain("zqmarker", fits.Error, StringComparison.Ordinal);

        // A text that cannot fit a small area is counted, and the area stays filled.
        var small = await RunAsync("redact", input, "--dry-run", "--rect", "1:15,38,8,8", "--overlay-text", new string('w', 150));
        Assert.True(small.ExitCode == 0, small.Error);
        Assert.Contains("1 overlay text does not fit its area", small.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public async Task HelpListsTheOverlayOptionsAndTheirLimits()
    {
        var result = await RunAsync("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--overlay-text <text>", result.Output, StringComparison.Ordinal);
        Assert.Contains("--overlay-repeat", result.Output, StringComparison.Ordinal);
        Assert.Contains("drawn as shapes", result.Output, StringComparison.Ordinal);
        Assert.Contains("not selectable", result.Output, StringComparison.Ordinal);
        Assert.Contains("does not read it back", result.Output, StringComparison.Ordinal);
    }
}

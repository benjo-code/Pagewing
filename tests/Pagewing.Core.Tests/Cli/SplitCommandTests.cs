using System.Globalization;
using System.Text;
using Pagewing.Core.Pdf;
using Xunit;

namespace Pagewing.Core.Tests.Cli;

/// <summary><c>pagewing split</c> (milestone 13, spec 5.6): every <i>n</i> pages, by ranges, by top-level bookmarks.</summary>
public sealed class SplitCommandTests : CliTestBase
{
    [Fact]
    public async Task SplitEveryTwoWritesThreeFiles()
    {
        var input = Copy("pages-marked.pdf");
        var output = Folder("out");
        var inputBytes = await File.ReadAllBytesAsync(input, Ct);
        string[] names = ["pages-marked-p001-002.pdf", "pages-marked-p003-004.pdf", "pages-marked-p005-006.pdf"];

        var result = await RunAsync("split", input, "--output", output, "--every", "2");

        Assert.Equal(0, result.ExitCode);

        // One written path per line, in the order of the parts, as the user named the folder.
        Assert.Equal(names.Select(name => Path.Combine(output, name)), result.OutputLines);
        Assert.Equal(names.Order(StringComparer.Ordinal), Directory.GetFiles(output).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        for (var part = 0; part < 3; part++)
        {
            var texts = await PageTextsAsync(Path.Combine(output, names[part]));
            Assert.Equal(2, texts.Length);
            Assert.Contains($"PAGE {(part * 2) + 1}", texts[0], StringComparison.Ordinal);
            Assert.Contains($"PAGE {(part * 2) + 2}", texts[1], StringComparison.Ordinal);
        }

        Assert.Equal(inputBytes, await File.ReadAllBytesAsync(input, Ct));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task SplitPrintsWhatEachPartLosesBeforeAnythingIsWritten()
    {
        var input = Copy("pages-marked.pdf");
        var output = Folder("out");
        int? filesWhenWarned = null;
        using var stderr = new ProbeWriter(() => filesWhenWarned ??= Directory.GetFiles(output).Length);

        var result = await RunAsync(stderr, environment: null, CancellationToken.None, "split", input, "--output", output, "--every", "3");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, filesWhenWarned);

        // The page labels do not follow the pages, and bookmarks to the other half stay without a target: said per file.
        Assert.Contains("warning: pages-marked-p004-006.pdf: ", result.Error, StringComparison.Ordinal);
        Assert.Contains("page labels", result.Error, StringComparison.Ordinal);
        Assert.Contains("warning: pages-marked-p001-003.pdf: Bookmarks that point to deleted pages", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SplitByRangesWritesOnePartPerRange()
    {
        var input = Copy("pages-marked.pdf");
        var output = Folder("out");

        var result = await RunAsync("split", input, "--output", output, "--ranges", "1-2,4-6");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            [Path.Combine(output, "pages-marked-p001-002.pdf"), Path.Combine(output, "pages-marked-p004-006.pdf")],
            result.OutputLines);
        Assert.Equal(3, (await PageTextsAsync(result.OutputLines[1])).Length);
    }

    [Theory]
    [InlineData("1-9", "Page 9 is out of range")]
    [InlineData("1-2,7", "Page 7 is out of range")]
    public async Task SplitRangesBeyondTheEndAreARuntimeErrorAndWriteNothing(string ranges, string message)
    {
        var input = Copy("pages-marked.pdf");
        var output = Folder("out");

        var result = await RunAsync("split", input, "--output", output, "--ranges", ranges);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(message, result.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SplitNamesAFolderInTheWayAsAFolderAlsoWithForce(bool force)
    {
        var input = Copy("pages-marked.pdf");
        var output = Folder("out");
        Directory.CreateDirectory(Path.Combine(output, "pages-marked-p001-002.pdf"));
        string[] args = force
            ? ["split", input, "--output", output, "--every", "2", "--force"]
            : ["split", input, "--output", output, "--every", "2"];

        var result = await RunAsync(args);

        // A folder is no "file that exists": --force would not help, so the message never asks for it on its account.
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("'pages-marked-p001-002.pdf' is a folder and cannot be replaced.", result.Error, StringComparison.Ordinal);
        Assert.Contains("Nothing was written.", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("already exist", result.Error, StringComparison.Ordinal);
        Assert.Equal([Path.Combine(output, "pages-marked-p001-002.pdf")], Directory.GetFileSystemEntries(output));
    }

    [Fact]
    public async Task SplitBookmarksWritesNamedFilesAndRefusesNone()
    {
        var input = Copy("pages-marked.pdf");
        var output = Folder("out");

        var result = await RunAsync("split", input, "--output", output, "--bookmarks");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            ["Kapitel 1.pdf", "Kapitel 2.pdf", "Kapitel 3.pdf"],
            result.OutputLines.Select(Path.GetFileName));
        Assert.Equal(["PAGE 1", "PAGE 2"], (await PageTextsAsync(Path.Combine(output, "Kapitel 1.pdf"))).Select(Squash));
        Assert.Equal(["PAGE 3", "PAGE 4"], (await PageTextsAsync(Path.Combine(output, "Kapitel 2.pdf"))).Select(Squash));
        Assert.Equal(["PAGE 5", "PAGE 6"], (await PageTextsAsync(Path.Combine(output, "Kapitel 3.pdf"))).Select(Squash));

        // A file without a top-level bookmark is an error with a message, never "one file".
        var plain = Copy("text-basic.pdf");
        var empty = Folder("none");
        var refused = await RunAsync("split", plain, "--output", empty, "--bookmarks");

        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("no top-level bookmark", refused.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(empty));
    }

    [Fact]
    public async Task SplitBookmarksPutThePagesBeforeTheFirstBookmarkInAFrontPart()
    {
        var input = WriteOutlinePdf("front.pdf", pages: 4, (2, "Chapter"));
        var output = Folder("out");

        var result = await RunAsync("split", input, "--output", output, "--bookmarks");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["front-front.pdf", "Chapter.pdf"], result.OutputLines.Select(Path.GetFileName));
    }

    [Fact]
    public async Task SplitNeverWritesOutsideTheFolder()
    {
        string[] titles = ["..\\..\\evil", "../../evil2", "CON", "a/b\\c:d*e?", "C:\\Windows\\win", "nul.pdf"];
        var input = WriteOutlinePdf("hostile.pdf", pages: titles.Length, titles.Select((title, index) => (index + 1, title)).ToArray());
        var output = Folder("deep/er/out");
        var decoys = new[] { WorkDirectory.FullName, Path.GetDirectoryName(output)!, Path.GetDirectoryName(Path.GetDirectoryName(output))! };
        var before = decoys.Select(folder => Directory.GetFileSystemEntries(folder).Order(StringComparer.Ordinal).ToArray()).ToArray();

        var result = await RunAsync("split", input, "--output", output, "--bookmarks");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(titles.Length, result.OutputLines.Length);
        var written = Directory.GetFiles(output);
        Assert.Equal(titles.Length, written.Length);
        foreach (var line in result.OutputLines)
        {
            Assert.Equal(Path.GetFullPath(output), Path.GetDirectoryName(Path.GetFullPath(line)));
            Assert.True(File.Exists(line));
        }

        // Nothing appeared next to the folder, above it, or beside the input.
        for (var i = 0; i < decoys.Length; i++)
        {
            var entries = Directory.GetFileSystemEntries(decoys[i]).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(before[i], entries);
        }

        Assert.Empty(TemporaryFiles());
    }

    [Theory]
    [InlineData("no-mode")]
    [InlineData("two-modes")]
    [InlineData("three-modes")]
    [InlineData("no-output")]
    [InlineData("every-zero")]
    [InlineData("every-text")]
    [InlineData("every-negative")]
    [InlineData("every-too-big")]
    [InlineData("ranges-bad")]
    [InlineData("ranges-empty-part")]
    [InlineData("ranges-empty")]
    [InlineData("no-file")]
    [InlineData("two-files")]
    [InlineData("bookmarks-with-value")]
    [InlineData("every-twice")]
    public async Task SplitNeedsExactlyOneModeAndAnOutput(string mistake)
    {
        var input = Copy("pages-marked.pdf");
        var output = Folder("out");
        string[] args = mistake switch
        {
            "no-mode" => ["split", input, "--output", output],
            "two-modes" => ["split", input, "--output", output, "--every", "2", "--bookmarks"],
            "three-modes" => ["split", input, "--output", output, "--every", "2", "--ranges", "1-2", "--bookmarks"],
            "no-output" => ["split", input, "--every", "2"],
            "every-zero" => ["split", input, "--output", output, "--every", "0"],
            "every-text" => ["split", input, "--output", output, "--every", "two"],
            "every-negative" => ["split", input, "--output", output, "--every", "-1"],
            "every-too-big" => ["split", input, "--output", output, "--every", "99999999999"],
            "ranges-bad" => ["split", input, "--output", output, "--ranges", "5-3"],
            "ranges-empty-part" => ["split", input, "--output", output, "--ranges", "1-2,,3"],
            "ranges-empty" => ["split", input, "--output", output, "--ranges", ""],
            "no-file" => ["split", "--output", output, "--every", "2"],
            "two-files" => ["split", input, input, "--output", output, "--every", "2"],
            "bookmarks-with-value" => ["split", input, "--output", output, "--bookmarks=yes"],
            _ => ["split", input, "--output", output, "--every", "2", "--every", "3"],
        };

        var result = await RunAsync(args);

        Assert.Equal(2, result.ExitCode);
        Assert.StartsWith("pagewing: ", result.Error, StringComparison.Ordinal);
        Assert.Contains("Usage:", result.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(output));
    }

    [Fact]
    public async Task SplitRefusesExistingFilesBeforeWritingAnythingAndNamesThem()
    {
        var input = Copy("pages-marked.pdf");
        var output = Folder("out");

        // Only the last target exists: nothing may be written before the refusal.
        var last = Path.Combine(output, "pages-marked-p005-006.pdf");
        await File.WriteAllTextAsync(last, "mine", Ct);

        var refused = await RunAsync("split", input, "--output", output, "--every", "2");

        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("pages-marked-p005-006.pdf", refused.Error, StringComparison.Ordinal);
        Assert.Contains("--force", refused.Error, StringComparison.Ordinal);
        Assert.Equal([last], Directory.GetFiles(output));
        Assert.Equal("mine", await File.ReadAllTextAsync(last, Ct));
        Assert.Equal(string.Empty, refused.Output);

        var forced = await RunAsync("split", input, "--output", output, "--every", "2", "--force");

        Assert.Equal(0, forced.ExitCode);
        Assert.Equal(3, Directory.GetFiles(output).Length);
        await using var replaced = await Engine.OpenAsync(last, cancellationToken: Ct);
        Assert.Equal(2, replaced.PageCount);
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task SplitNeverWritesOverTheInputNotEvenWithForce()
    {
        // The bookmark title "Kapitel 1" names the first part like the input file.
        var folder = Folder("work");
        var input = Path.Combine(folder, "Kapitel 1.pdf");
        File.Copy(Pagewing.TestSupport.TestData.Pdf("pages-marked.pdf"), input);
        var before = await File.ReadAllBytesAsync(input, Ct);

        var result = await RunAsync("split", input, "--output", folder, "--bookmarks", "--force");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("must not replace the input file", result.Error, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(input, Ct));
        Assert.Equal([input], Directory.GetFiles(folder));
    }

    [Fact]
    public async Task SplitNamesAFolderThatDoesNotExistAndDoesNotCreateIt()
    {
        var input = Copy("pages-marked.pdf");
        var missing = Path.Combine(WorkDirectory.FullName, "missing");

        var result = await RunAsync("split", input, "--output", missing, "--every", "2");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("does not exist", result.Error, StringComparison.Ordinal);
        Assert.Contains(missing, result.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task SplitRefusesAFileAsTheOutputFolder()
    {
        var input = Copy("pages-marked.pdf");

        var result = await RunAsync("split", input, "--output", input, "--every", "2");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("does not exist", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("encrypted_hello_world_r3.pdf")]
    [InlineData("encrypted_hello_world_r6.pdf")]
    public async Task SplitOfAnEncryptedFileNeedsThePasswordAndKeepsEncryption(string fixture)
    {
        var input = Copy(fixture);
        var output = Folder("out");

        var without = await RunAsync("split", input, "--output", output, "--every", "1");
        var wrong = await RunAsync("split", input, "--output", output, "--every", "1", "--password", "wrong");
        var good = await RunAsync(new Dictionary<string, string> { ["PAGEWING_PASSWORD"] = UserPassword }, "split", input, "--output", output, "--every", "1");

        Assert.Equal(1, without.ExitCode);
        Assert.Contains("Password required", without.Error, StringComparison.Ordinal);
        Assert.Equal(1, wrong.ExitCode);
        Assert.Equal("pagewing: The password is incorrect.", wrong.Error.TrimEnd());
        Assert.Equal(0, good.ExitCode);
        Assert.DoesNotContain(UserPassword, good.Error + good.Output, StringComparison.Ordinal);
        var part = Assert.Single(good.OutputLines);
        var closed = await Assert.ThrowsAsync<PdfOpenException>(() => Engine.OpenAsync(part, cancellationToken: Ct));
        Assert.Equal(PdfOpenError.PasswordRequired, closed.Error);
        await using var opened = await Engine.OpenAsync(part, UserPassword, Ct);
        Assert.Equal(1, opened.PageCount);
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task SplitOfASignedFileWarnsThatTheSignatureIsShownAsInvalid()
    {
        var input = Copy("signed-docmdp-1.pdf");
        var output = Folder("out");

        var result = await RunAsync("split", input, "--output", output, "--every", "1");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("digitally signed", result.Error, StringComparison.Ordinal);
        Assert.Contains("shown as invalid", result.Error, StringComparison.Ordinal);
        Assert.Contains("warning: signed-docmdp-1-p001.pdf: ", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SplitReportsTheFilesAlreadyWrittenWhenAPartFails()
    {
        var input = Copy("pages-marked.pdf");
        var output = Folder("out");

        // The input is changed by another program after the first part: the second part fails with that reason.
        var changed = false;
        var progress = new CallbackProgress(value =>
        {
            if (value > (1.0 / 3) + 1e-9 && !changed)
            {
                changed = true;
                File.AppendAllText(input, "\n% changed by another program\n");
            }
        });

        var result = await RunWithProgressAsync(progress, Ct, "split", input, "--output", output, "--every", "2");

        var first = Path.Combine(output, "pages-marked-p001-002.pdf");
        Assert.Equal(1, result.ExitCode);
        Assert.True(changed);
        Assert.Contains("pages-marked-p003-004.pdf", result.Error, StringComparison.Ordinal);
        Assert.Contains("changed by another program", result.Error, StringComparison.Ordinal);
        Assert.Contains("1 of 3 files were written", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("DocumentSplitException", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("A part could not be written", result.Error, StringComparison.Ordinal);

        // What exists is on stdout, and nothing else is in the folder.
        Assert.Equal([first], result.OutputLines);
        Assert.Equal([first], Directory.GetFiles(output));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task SplitCanceledKeepsTheFinishedPartsAndReportsThem()
    {
        var input = Copy("pages-marked.pdf");
        var output = Folder("out");
        using var source = new CancellationTokenSource();
        var progress = new CallbackProgress(value =>
        {
            if (value > (1.0 / 3) + 1e-9)
            {
                source.Cancel();
            }
        });

        var result = await RunWithProgressAsync(progress, source.Token, "split", input, "--output", output, "--every", "2");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("pagewing: Canceled.", result.Error, StringComparison.Ordinal);
        Assert.Contains("1 of 3 files were written", result.Error, StringComparison.Ordinal);
        var written = Directory.GetFiles(output);
        Assert.Single(written);
        Assert.Equal(written, result.OutputLines);
        Assert.Empty(TemporaryFiles());
    }

    private static string Squash(string text) => text.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();

    /// <summary>Writes a PDF of blank pages with top-level bookmarks (page numbers count from 1; titles are written as UTF-16 strings).</summary>
    private string WriteOutlinePdf(string name, int pages, params (int Page, string Title)[] bookmarks)
    {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R /Outlines " + (3 + pages) + " 0 R >>" };
        objects.Add("<< /Type /Pages /Kids [" + string.Join(' ', Enumerable.Range(0, pages).Select(i => $"{3 + i} 0 R")) + $"] /Count {pages} >>");
        for (var i = 0; i < pages; i++)
        {
            objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>");
        }

        var first = 4 + pages;
        objects.Add(string.Create(CultureInfo.InvariantCulture, $"<< /Type /Outlines /First {first} 0 R /Last {first + bookmarks.Length - 1} 0 R /Count {bookmarks.Length} >>"));
        for (var i = 0; i < bookmarks.Length; i++)
        {
            var self = first + i;
            var links = (i > 0 ? $" /Prev {self - 1} 0 R" : string.Empty) + (i < bookmarks.Length - 1 ? $" /Next {self + 1} 0 R" : string.Empty);
            objects.Add($"<< /Title <{Hex(bookmarks[i].Title)}> /Parent {3 + pages} 0 R{links} /Dest [{2 + bookmarks[i].Page} 0 R /Fit] >>");
        }

        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        var path = Path.Combine(WorkDirectory.FullName, name);
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(pdf.ToString()));
        return path;
    }

    private static string Hex(string text) => "FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text));
}

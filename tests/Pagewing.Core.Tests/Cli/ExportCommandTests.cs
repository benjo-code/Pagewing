using Xunit;

namespace Pagewing.Core.Tests.Cli;

/// <summary><c>pagewing export</c> (milestone 13, spec 5.6): the images of <c>render</c>, with the rule that nothing is replaced without <c>--force</c>.</summary>
public sealed class ExportCommandTests : CliTestBase
{
    [Fact]
    public async Task ExportWritesPngWithRenderNamesAndForceRules()
    {
        var input = Copy("pages-marked.pdf");
        var output = Folder("images");
        string[] names = ["pages-marked-p001.png", "pages-marked-p002.png", "pages-marked-p003.png"];

        var result = await RunAsync("export", input, "--pages", "1-3", "--dpi", "36", "--output", output);

        // The names are the names render gives, the paths are printed as the user named the folder.
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(names.Select(name => Path.Combine(output, name)), result.OutputLines);
        Assert.Equal(string.Empty, result.Error);
        foreach (var line in result.OutputLines)
        {
            Assert.Equal([0x89, 0x50, 0x4E, 0x47], (await File.ReadAllBytesAsync(line, Ct))[..4].Select(static b => (int)b));
        }

        // Without --force nothing is replaced, and nothing at all is written when one file is in the way.
        var second = Path.Combine(output, names[1]);
        var before = await File.ReadAllBytesAsync(second, Ct);
        await File.WriteAllTextAsync(Path.Combine(output, names[2]), "mine", Ct);
        File.Delete(Path.Combine(output, names[0]));
        var refused = await RunAsync("export", input, "--pages", "1-3", "--dpi", "36", "--output", output);

        Assert.Equal(1, refused.ExitCode);
        Assert.Contains(names[1], refused.Error, StringComparison.Ordinal);
        Assert.Contains(names[2], refused.Error, StringComparison.Ordinal);
        Assert.Contains("--force", refused.Error, StringComparison.Ordinal);
        Assert.Equal(string.Empty, refused.Output);
        Assert.False(File.Exists(Path.Combine(output, names[0])));
        Assert.Equal(before, await File.ReadAllBytesAsync(second, Ct));
        Assert.Equal("mine", await File.ReadAllTextAsync(Path.Combine(output, names[2]), Ct));

        var forced = await RunAsync("export", input, "--pages", "1-3", "--dpi", "36", "--output", output, "--force");

        Assert.Equal(0, forced.ExitCode);
        Assert.Equal(3, forced.OutputLines.Length);
        Assert.Equal(before, await File.ReadAllBytesAsync(second, Ct));
        Assert.NotEqual("mine", await File.ReadAllTextAsync(Path.Combine(output, names[2]), Ct));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task ExportWritesTheSameImagesAsRender()
    {
        var input = Copy("annotations.pdf");
        var viaRender = Folder("render");
        var viaExport = Folder("export");

        var render = await RunAsync("render", input, "--dpi", "50", "--output", viaRender);
        var export = await RunAsync("export", input, "--dpi", "50", "--output", viaExport);

        Assert.Equal(0, render.ExitCode);
        Assert.Equal(0, export.ExitCode);
        Assert.Equal(render.OutputLines.Select(Path.GetFileName), export.OutputLines.Select(Path.GetFileName));
        foreach (var name in export.OutputLines.Select(Path.GetFileName))
        {
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(viaRender, name!), Ct), await File.ReadAllBytesAsync(Path.Combine(viaExport, name!), Ct));
        }

        File.Delete(Path.Combine(viaRender, Path.GetFileName(render.OutputLines[0])));
        var plainRender = await RunAsync("render", input, "--dpi", "50", "--no-annotations", "--pages", "1", "--output", viaRender);
        var plainExport = await RunAsync("export", input, "--dpi", "50", "--no-annotations", "--pages", "1", "--output", Folder("plain"));
        Assert.Equal(0, plainRender.ExitCode);
        Assert.Equal(0, plainExport.ExitCode);
        Assert.Equal(await File.ReadAllBytesAsync(plainRender.OutputLines[0], Ct), await File.ReadAllBytesAsync(plainExport.OutputLines[0], Ct));
    }

    [Fact]
    public async Task ExportCreatesAMissingFolderLikeRender()
    {
        var input = Copy("pages-marked.pdf");
        var output = Path.Combine(WorkDirectory.FullName, "new", "images");

        var result = await RunAsync("export", input, "--pages", "1", "--dpi", "36", "--output", output);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(output, "pages-marked-p001.png")));
    }

    [Fact]
    public async Task ExportRefusesAPageAboveTheLimitAndWritesNothing()
    {
        var input = Copy("pages-marked.pdf");
        var output = Folder("images");

        var result = await RunAsync("export", input, "--dpi", "5000", "--output", output);

        Assert.Equal(1, result.ExitCode);
        Assert.StartsWith("pagewing: Page 1 would have", result.Error, StringComparison.Ordinal);
        Assert.Contains("megapixels", result.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(output));
    }

    [Theory]
    [InlineData("--dpi", "0", 2)]
    [InlineData("--dpi", "abc", 2)]
    [InlineData("--dpi", "NaN", 2)]
    [InlineData("--dpi", "Infinity", 2)]
    [InlineData("--pages", "5-3", 2)]
    [InlineData("--pages", "abc", 2)]
    [InlineData("--pages", "9", 1)]
    public async Task ExportRejectsBadOptions(string option, string value, int exitCode)
    {
        var input = Copy("pages-marked.pdf");
        var output = Folder("images");

        var result = await RunAsync("export", input, option, value, "--output", output);

        Assert.Equal(exitCode, result.ExitCode);
        Assert.StartsWith("pagewing: ", result.Error, StringComparison.Ordinal);
        Assert.Equal(exitCode == 2, result.Error.Contains("Usage:", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFileSystemEntries(output));
    }

    [Fact]
    public async Task ExportNeedsAFileAndTakesOnlyOne()
    {
        var input = Copy("pages-marked.pdf");

        var none = await RunAsync("export", "--dpi", "36");
        var two = await RunAsync("export", input, input);

        Assert.Equal(2, none.ExitCode);
        Assert.Equal(2, two.ExitCode);
    }

    [Fact]
    public async Task ExportOfAnEncryptedFileNeedsThePassword()
    {
        var input = Copy("encrypted_hello_world_r3.pdf");
        var output = Folder("images");

        var without = await RunAsync("export", input, "--dpi", "36", "--output", output);
        var good = await RunAsync(new Dictionary<string, string> { ["PAGEWING_PASSWORD"] = UserPassword }, "export", input, "--dpi", "36", "--output", output);

        Assert.Equal(1, without.ExitCode);
        Assert.Contains("Password required", without.Error, StringComparison.Ordinal);
        Assert.Equal(0, good.ExitCode);
        Assert.Single(good.OutputLines);
    }

    [Fact]
    public async Task ExportCanceledKeepsTheFinishedImagesAndReportsThem()
    {
        var input = Copy("pages-marked.pdf");
        var output = Folder("images");
        using var source = new CancellationTokenSource();

        // The progress after the first page is 1/6; the cancel comes right then.
        var progress = new CallbackProgress(value =>
        {
            if (value > 0)
            {
                source.Cancel();
            }
        });

        var result = await RunWithProgressAsync(progress, source.Token, "export", input, "--dpi", "36", "--output", output);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("pagewing: Canceled.", result.Error, StringComparison.Ordinal);
        var written = Directory.GetFiles(output);
        Assert.NotEmpty(written);
        Assert.Contains(" of 6 files were written", result.Error, StringComparison.Ordinal);
        Assert.Equal(written.Length, result.OutputLines.Length);
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task ExportNeverReplacesAFileThatAppearsWhileItRuns()
    {
        var input = Copy("pages-marked.pdf");
        var output = Folder("images");
        var target = Path.Combine(output, "pages-marked-p002.png");
        var foreign = "foreign file of another program"u8.ToArray();

        // Page 1 is done; another program creates the file of page 2 before it is moved into place.
        var progress = new CallbackProgress(value =>
        {
            if (value > 0 && !File.Exists(target))
            {
                File.WriteAllBytes(target, foreign);
            }
        });

        var result = await RunWithProgressAsync(progress, Ct, "export", input, "--dpi", "36", "--pages", "1-2", "--output", output);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("pages-marked-p002.png", result.Error, StringComparison.Ordinal);
        Assert.Equal(foreign, await File.ReadAllBytesAsync(target, Ct));
        Assert.Empty(TemporaryFiles());
    }
}

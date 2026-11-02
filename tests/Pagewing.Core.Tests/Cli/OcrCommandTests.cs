using Pagewing.Cli.Commands;
using Pagewing.Core.Ocr;
using Pagewing.Core.Tests.Ocr;
using Pagewing.Ocr;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Cli;

/// <summary>
/// <c>pagewing ocr</c> (milestone 15). Most tests use a fake engine (they run without the native library); two run the real one and skip without it.
/// The fake is put in the seam of <see cref="OcrCommand"/>, which is global: the tests of this class run one after the other, and each puts it back.
/// </summary>
public sealed class OcrCommandTests : CliTestBase
{
    private readonly string tessdata;

    public OcrCommandTests()
    {
        // A folder of two language files that only have to exist (the fake engine never reads them).
        tessdata = Folder("tessdata");
        File.WriteAllBytes(Path.Combine(tessdata, "eng.traineddata"), [1]);
        File.WriteAllBytes(Path.Combine(tessdata, "deu.traineddata"), [2]);
    }

    private static OcrRecognition One(string text = "Invoice") => new([new OcrWord(text, 100, 100, 300, 150, 90)]);

    /// <summary>Puts a fake engine factory in the seam until the returned object is disposed.</summary>
    private static Seam UseFake(Func<FakeCall, OcrRecognition>? behavior = null)
    {
        var factory = new FakeOcrFactory(behavior ?? (_ => One()));
        OcrCommand.FactoryOverride = _ => factory;
        return new Seam(factory);
    }

    private sealed class Seam(FakeOcrFactory factory) : IDisposable
    {
        public FakeOcrFactory Factory { get; } = factory;

        public void Dispose() => OcrCommand.FactoryOverride = null;
    }

    [Fact]
    public async Task RecognisesAScanAndTheOutputIsSearchable()
    {
        OcrTestLibrary.Require();
        var input = Copy("ocr-scan.pdf");
        var output = Path.Combine(WorkDirectory.FullName, "out.pdf");

        var result = await RunAsync("ocr", input, "--output", output, "--language", "eng+deu", "--tessdata", OcrTestLibrary.Tessdata);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([output], result.OutputLines);
        Assert.StartsWith("Recognized 3 pages", result.Error, StringComparison.Ordinal);
        var texts = await PageTextsAsync(output);
        Assert.Contains("Invoice", texts[0], StringComparison.Ordinal);
        Assert.Contains("Müller", texts[1], StringComparison.Ordinal);
        Assert.Contains("column", texts[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportContainsNoRecognisedText()
    {
        OcrTestLibrary.Require();
        var input = Copy("ocr-scan.pdf");
        var output = Path.Combine(WorkDirectory.FullName, "out.pdf");

        var result = await RunAsync("ocr", input, "--output", output, "--language", "eng+deu", "--tessdata", OcrTestLibrary.Tessdata);

        Assert.Equal(0, result.ExitCode);
        foreach (var secret in new[] { "Invoice", "Müller", "quick", "alpha", "business" })
        {
            Assert.DoesNotContain(secret, result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, result.Output, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task OutputKeepsTheInputBytesAsPrefix()
    {
        using var fake = UseFake();
        var input = Copy("ocr-scan.pdf");
        var original = await File.ReadAllBytesAsync(input, Ct);
        var output = Path.Combine(WorkDirectory.FullName, "out.pdf");

        var result = await RunAsync("ocr", input, "--output", output, "--tessdata", tessdata);

        Assert.Equal(0, result.ExitCode);
        var written = await File.ReadAllBytesAsync(output, Ct);
        Assert.True(written.Length > original.Length);
        Assert.True(written.AsSpan(0, original.Length).SequenceEqual(original), "the input bytes are not the beginning of the output");
        Assert.Equal(original, await File.ReadAllBytesAsync(input, Ct));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task TheLayerIsWrittenAtThePlaceOfTheWord()
    {
        using var fake = UseFake();
        var input = Copy("ocr-scan.pdf");
        var output = Path.Combine(WorkDirectory.FullName, "out.pdf");

        await RunAsync("ocr", input, "--output", output, "--tessdata", tessdata, "--pages", "1");

        await using var document = await Engine.OpenAsync(output, cancellationToken: Ct);
        var layer = await document.ReadOcrLayerAsync(0, Ct);
        var word = Assert.Single(layer);
        Assert.Equal("Invoice", word.Text);
        // The fake's box (pixels 100,100 to 300,150 of a 200 dpi page) is 36,36 to 108,54 points.
        Assert.Equal(36, word.Left, 0.5);
        Assert.Equal(36, word.Top, 0.5);
        Assert.Equal(108, word.Right, 0.5);
        Assert.Equal(54, word.Bottom, 0.5);
        Assert.Empty(await document.ReadOcrLayerAsync(1, Ct));
    }

    [Fact]
    public async Task PagesWithTextAreSkippedAndReportedOnStderr()
    {
        using var fake = UseFake();
        var input = Copy("ocr-mixed.pdf");
        var output = Path.Combine(WorkDirectory.FullName, "out.pdf");

        var result = await RunAsync("ocr", input, "--output", output, "--tessdata", tessdata);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Skipped 1 page:", result.Error, StringComparison.Ordinal);
        Assert.Contains("page 1: has text of its own", result.Error, StringComparison.Ordinal);
        Assert.Contains("page 2: has a little text of its own", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownLanguageExits1ListingTheInstalled()
    {
        using var fake = UseFake();
        var input = Copy("ocr-scan.pdf");
        var output = Path.Combine(WorkDirectory.FullName, "out.pdf");

        var result = await RunAsync("ocr", input, "--output", output, "--language", "eng+klingon", "--tessdata", tessdata);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("klingon", result.Error, StringComparison.Ordinal);
        Assert.Contains("installed: deu, eng", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
        Assert.Equal(0, fake.Factory.Created);
    }

    [Fact]
    public async Task MissingLibraryExits1WithAMessage()
    {
        OcrCommand.FactoryOverride = _ => new MissingLibraryFactory();
        try
        {
            var input = Copy("ocr-scan.pdf");
            var output = Path.Combine(WorkDirectory.FullName, "out.pdf");

            var result = await RunAsync("ocr", input, "--output", output, "--tessdata", tessdata);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("OCR library", result.Error, StringComparison.Ordinal);
            Assert.Contains("Nothing was written", result.Error, StringComparison.Ordinal);
            Assert.False(File.Exists(output));
            Assert.DoesNotContain("Internal error", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            OcrCommand.FactoryOverride = null;
        }
    }

    [Fact]
    public async Task OutputEqualToInputIsRefusedAndForceNeverReplacesAnInput()
    {
        using var fake = UseFake();
        var input = Copy("ocr-scan.pdf");
        var original = await File.ReadAllBytesAsync(input, Ct);

        var plain = await RunAsync("ocr", input, "--output", input, "--tessdata", tessdata);
        var forced = await RunAsync("ocr", input, "--output", input, "--force", "--tessdata", tessdata);

        Assert.Equal(1, plain.ExitCode);
        Assert.Equal(1, forced.ExitCode);
        Assert.Contains("must not be the input", plain.Error, StringComparison.Ordinal);
        Assert.Contains("must not be the input", forced.Error, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllBytesAsync(input, Ct));
        Assert.Equal(0, fake.Factory.Created);
    }

    [Fact]
    public async Task AnExistingOutputIsOnlyReplacedWithForce()
    {
        using var fake = UseFake();
        var input = Copy("ocr-scan.pdf");
        var output = Path.Combine(WorkDirectory.FullName, "out.pdf");
        await File.WriteAllTextAsync(output, "an earlier result", Ct);

        var refused = await RunAsync("ocr", input, "--output", output, "--tessdata", tessdata);

        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("already exists", refused.Error, StringComparison.Ordinal);
        Assert.Equal("an earlier result", await File.ReadAllTextAsync(output, Ct));

        var replaced = await RunAsync("ocr", input, "--output", output, "--force", "--tessdata", tessdata);

        Assert.Equal(0, replaced.ExitCode);
        Assert.True(new FileInfo(output).Length > 1000);
    }

    [Fact]
    public async Task DryRunLoadsNoLibraryAndListsPagesAndDpi()
    {
        var calls = 0;
        OcrCommand.FactoryOverride = _ =>
        {
            Interlocked.Increment(ref calls);
            return new MissingLibraryFactory();
        };
        try
        {
            var input = Copy("ocr-mixed.pdf");

            var result = await RunAsync("ocr", input, "--dry-run", "--tessdata", tessdata);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(
                [
                    "page 1: skip (has text of its own)",
                    "page 2: recognize at 200 dpi; has a little text of its own, which stays",
                    "page 3: recognize at 300 dpi",
                    "would recognize 2 pages",
                ],
                result.OutputLines);
            Assert.Equal(0, calls);
            Assert.Equal([input], Directory.GetFiles(WorkDirectory.FullName, "*.pdf"));
        }
        finally
        {
            OcrCommand.FactoryOverride = null;
        }
    }

    [Fact]
    public async Task DryRunNamesAPageThatIsTooLarge()
    {
        var input = Copy("ocr-hostile.pdf");

        var result = await RunAsync("ocr", input, "--dry-run", "--tessdata", tessdata, "--pages", "1");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("page 1: skip (too large to recognize (", result.Output, StringComparison.Ordinal);
        Assert.Contains("would recognize 0 pages", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoRecognisedPageExits1()
    {
        using var fake = UseFake();
        var input = Copy("ocr-mixed.pdf");
        var output = Path.Combine(WorkDirectory.FullName, "out.pdf");

        var result = await RunAsync("ocr", input, "--output", output, "--tessdata", tessdata, "--pages", "1");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("No page was recognized; nothing was written.", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task APageRunWithoutAnyWordWritesNothing()
    {
        using var fake = UseFake(_ => new OcrRecognition([]));
        var input = Copy("ocr-scan.pdf");
        var output = Path.Combine(WorkDirectory.FullName, "out.pdf");

        var result = await RunAsync("ocr", input, "--output", output, "--tessdata", tessdata);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("No text was found", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task APageWhoseWordsTheFontCannotCarryExits1AndWritesNothing()
    {
        using var fake = UseFake(_ => One("Привет"));
        var input = Copy("ocr-scan.pdf");
        var output = Path.Combine(WorkDirectory.FullName, "out.pdf");

        var result = await RunAsync("ocr", input, "--output", output, "--tessdata", tessdata);

        Assert.Equal(1, result.ExitCode);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task ReportNeverContainsTheTextTheEngineFound()
    {
        using var fake = UseFake(_ => One("Confidential4711"));
        var input = Copy("ocr-scan.pdf");
        var output = Path.Combine(WorkDirectory.FullName, "out.pdf");

        var result = await RunAsync("ocr", input, "--output", output, "--tessdata", tessdata);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("Confidential4711", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Confidential4711", result.Output, StringComparison.Ordinal);
        Assert.Contains("mean confidence 90 %", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--mode", "fast")]
    [InlineData("--dpi", "100")]
    [InlineData("--dpi", "abc")]
    [InlineData("--dpi", "601")]
    [InlineData("--jobs", "0")]
    [InlineData("--jobs", "99")]
    [InlineData("--language", "../eng")]
    [InlineData("--language", "ENG")]
    [InlineData("--tessdata", "")]
    public async Task BadOptionsExit2(string option, string value)
    {
        var input = Copy("ocr-scan.pdf");
        var output = Path.Combine(WorkDirectory.FullName, "out.pdf");

        var result = await RunAsync("ocr", input, "--output", output, option, value);

        Assert.Equal(2, result.ExitCode);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task AMissingOutputAndAnOptionGivenTwiceExit2()
    {
        var input = Copy("ocr-scan.pdf");

        var withoutOutput = await RunAsync("ocr", input);
        var twice = await RunAsync("ocr", input, "--dry-run", "--mode", "skip", "--mode", "redo");

        Assert.Equal(2, withoutOutput.ExitCode);
        Assert.Contains("needs --output", withoutOutput.Error, StringComparison.Ordinal);
        Assert.Equal(2, twice.ExitCode);
        Assert.Contains("given twice", twice.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASignedFileWarnsBeforeTheLayerIsWritten()
    {
        using var fake = UseFake();
        var input = Copy("signed-docmdp-2.pdf");
        var output = Path.Combine(WorkDirectory.FullName, "out.pdf");

        var result = await RunAsync("ocr", input, "--output", output, "--tessdata", tessdata, "--mode", "force");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("warning: the file has a digital signature that allows no changes", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASecondRunSkipsPagesWithALayerUnlessRedoReplacesIt()
    {
        using var fake = UseFake();
        var input = Copy("ocr-scan.pdf");
        var first = Path.Combine(WorkDirectory.FullName, "first.pdf");
        await RunAsync("ocr", input, "--output", first, "--tessdata", tessdata, "--pages", "1");

        var plain = await RunAsync("ocr", first, "--output", Path.Combine(WorkDirectory.FullName, "again.pdf"), "--tessdata", tessdata, "--pages", "1");
        var redo = Path.Combine(WorkDirectory.FullName, "redo.pdf");
        var replaced = await RunAsync("ocr", first, "--output", redo, "--tessdata", tessdata, "--pages", "1", "--mode", "redo");

        Assert.Equal(1, plain.ExitCode);
        Assert.Contains("already has an OCR text layer", plain.Error, StringComparison.Ordinal);
        Assert.Equal(0, replaced.ExitCode);
        await using var document = await Engine.OpenAsync(redo, cancellationToken: Ct);
        Assert.Single(await document.ReadOcrLayerAsync(0, Ct));
    }

    [Fact]
    public async Task HelpAndUsageNameTheCommand()
    {
        var help = await RunAsync("--help");

        Assert.Equal(0, help.ExitCode);
        Assert.Contains("pagewing ocr", help.Output, StringComparison.Ordinal);
        Assert.Contains("--tessdata", help.Output, StringComparison.Ordinal);
    }

    private sealed class MissingLibraryFactory : IOcrEngineFactory
    {
        public IOcrEngine Create(string languages) => throw new OcrEngineException(OcrEngineError.LibraryMissing, "The OCR library is not there.");
    }
}

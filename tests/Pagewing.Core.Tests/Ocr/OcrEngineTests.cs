using Pagewing.Ocr;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Ocr;

/// <summary>The engine against the real library (they skip without it, see <see cref="OcrTestLibrary"/>) and the rules that need none.</summary>
public sealed class OcrEngineTests
{
    private const int Dpi = 300;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void LibraryIsNotLoadedBeforeFirstUse()
    {
        OcrTestLibrary.Require();

        // Looking whether the library is there loads nothing; only Load (the first engine) does.
        var library = new TesseractLibrary(OcrTestLibrary.Directory!);
        Assert.True(library.IsAvailable);
        Assert.False(library.IsLoaded);
        library.Load();
        Assert.True(library.IsLoaded);
    }

    [Fact]
    public void LibraryIsOnlyLookedForInTheGivenFolder()
    {
        using var folder = new TempFolderHere();
        var library = new TesseractLibrary(folder.Path);

        Assert.False(library.IsAvailable);
        var failure = Assert.Throws<OcrEngineException>(() => library.Load());
        Assert.Equal(OcrEngineError.LibraryMissing, failure.Error);
        Assert.False(library.IsLoaded);
    }

    [Fact]
    public async Task RecognizesAGeneratedWordImage()
    {
        OcrTestLibrary.Require();
        var pdf = TestData.Pdf("text-basic.pdf");
        var (gray, width, height, scale) = await OcrTestImages.RenderGrayAsync(pdf, 0, Dpi, Ct);

        using var engine = OcrEngine.Create(OcrTestLibrary.Tessdata, "eng");
        var result = engine.Recognize(gray, width, height, Dpi, Ct);

        Assert.Contains(result.Words, static w => w.Text.StartsWith("Hello", StringComparison.Ordinal));
        var page = Assert.Single(result.Words, static w => w.Text == "Pagewing");
        var known = await OcrTestImages.WordBoxAsync(pdf, 0, "Pagewing", Ct);
        const double Tolerance = 8; // pixels at 300 dpi; PDFium's character boxes include the side bearings, the engine's boxes are the ink
        Assert.InRange(page.Left, (known.Left * scale) - Tolerance, (known.Left * scale) + Tolerance);
        Assert.InRange(page.Right, (known.Right * scale) - Tolerance, (known.Right * scale) + Tolerance);
        Assert.InRange(page.Top, (known.Top * scale) - Tolerance * 3, (known.Bottom * scale));
        Assert.InRange(page.Bottom, (known.Top * scale), (known.Bottom * scale) + Tolerance * 3);
        Assert.InRange(page.Confidence, 50, 100);
    }

    [Fact]
    public async Task GermanUmlautsAreRecognized()
    {
        OcrTestLibrary.Require();
        var (gray, width, height, _) = await OcrTestImages.RenderGrayAsync(TestData.Pdf("text-basic.pdf"), 1, Dpi, Ct);

        using var engine = OcrEngine.Create(OcrTestLibrary.Tessdata, "deu");
        var result = engine.Recognize(gray, width, height, Dpi, Ct);

        Assert.Contains(result.Words, static w => w.Text.StartsWith("Größe", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownLanguageThrowsAndNamesTheInstalledOnes()
    {
        OcrTestLibrary.Require();

        var failure = Assert.Throws<OcrEngineException>(() => OcrEngine.Create(OcrTestLibrary.Tessdata, "eng+klingon"));

        Assert.Equal(OcrEngineError.LanguageMissing, failure.Error);
        Assert.Contains("klingon", failure.Message, StringComparison.Ordinal);
        Assert.Contains("deu", failure.InstalledLanguages);
        Assert.Contains("eng", failure.InstalledLanguages);
        Assert.Contains("eng", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("eng+")]
    [InlineData("../eng")]
    [InlineData("eng deu")]
    [InlineData("ENG")]
    public void LanguageNamesOutsideTheAllowedSetAreRefused(string languages)
    {
        OcrTestLibrary.Require();

        var failure = Assert.Throws<OcrEngineException>(() => OcrEngine.Create(OcrTestLibrary.Tessdata, languages));

        Assert.Equal(OcrEngineError.LanguageInvalid, failure.Error);
    }

    [Fact]
    public void CorruptTraineddataThrowsOcrEngineExceptionNotACrash()
    {
        OcrTestLibrary.Require();
        using var folder = new TempFolderHere();
        var bytes = new byte[4096];
        new Random(15).NextBytes(bytes);
        File.WriteAllBytes(Path.Combine(folder.Path, "xyz.traineddata"), bytes);

        var failure = Assert.Throws<OcrEngineException>(() => OcrEngine.Create(folder.Path, "xyz"));

        Assert.Equal(OcrEngineError.DataCorrupt, failure.Error);
    }

    [Fact]
    public async Task CancellationBetweenCallsIsHonoured()
    {
        OcrTestLibrary.Require();
        var (gray, width, height, _) = await OcrTestImages.RenderGrayAsync(TestData.Pdf("text-basic.pdf"), 0, Dpi, Ct);
        using var engine = OcrEngine.Create(OcrTestLibrary.Tessdata, "eng");
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        Assert.Throws<OperationCanceledException>(() => engine.Recognize(gray, width, height, Dpi, canceled.Token));

        // The engine is still usable after a canceled call.
        Assert.NotEmpty(engine.Recognize(gray, width, height, Dpi, Ct).Words);
    }

    [Fact]
    public async Task ABlankPageGivesNoWordsAndNoError()
    {
        OcrTestLibrary.Require();
        var gray = new byte[1200 * 1600];
        Array.Fill(gray, (byte)255);
        using var engine = OcrEngine.Create(OcrTestLibrary.Tessdata, "eng");

        var result = engine.Recognize(gray, 1200, 1600, Dpi, Ct);

        Assert.Empty(result.Words);
        await Task.CompletedTask;
    }

    [Fact]
    public void ABufferThatIsNotWidthTimesHeightIsRefused()
    {
        OcrTestLibrary.Require();
        using var engine = OcrEngine.Create(OcrTestLibrary.Tessdata, "eng");

        Assert.Throws<ArgumentException>(() => engine.Recognize(new byte[100], 20, 20, Dpi, Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.Recognize(new byte[400], 20, 20, 0, Ct));
    }

    [Fact]
    public async Task NoNetworkAndNoFilesAreWritten()
    {
        OcrTestLibrary.Require();
        var (gray, width, height, _) = await OcrTestImages.RenderGrayAsync(TestData.Pdf("text-basic.pdf"), 0, Dpi, Ct);
        using var workingDirectory = new TempFolderHere();
        var temp = Path.GetTempPath();
        var tempBefore = Directory.GetFileSystemEntries(temp).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tessdataBefore = Directory.GetFileSystemEntries(OcrTestLibrary.Tessdata).Order().ToArray();
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = workingDirectory.Path;
        try
        {
            using var engine = OcrEngine.Create(OcrTestLibrary.Tessdata, "eng");
            Assert.NotEmpty(engine.Recognize(gray, width, height, Dpi, Ct).Words);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }

        Assert.Empty(Directory.GetFileSystemEntries(workingDirectory.Path));
        Assert.Equal(tessdataBefore, Directory.GetFileSystemEntries(OcrTestLibrary.Tessdata).Order().ToArray());
        Assert.DoesNotContain(
            Directory.GetFileSystemEntries(temp),
            entry => !tempBefore.Contains(entry) && Path.GetFileName(entry).Contains("tess", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ATessdataFolderWithNonAsciiCharactersWorks()
    {
        // The folder of a user named Müller: the engine takes a UTF-8 path, which a Windows build may read as ANSI.
        OcrTestLibrary.Require();
        using var folder = new TempFolderHere();
        var tessdata = Directory.CreateDirectory(Path.Combine(folder.Path, "Müller – Ünïcode", "tessdata")).FullName;
        File.Copy(Path.Combine(OcrTestLibrary.Tessdata, "eng.traineddata"), Path.Combine(tessdata, "eng.traineddata"));
        var (gray, width, height, _) = await OcrTestImages.RenderGrayAsync(TestData.Pdf("text-basic.pdf"), 0, Dpi, Ct);

        using var engine = OcrEngine.Create(tessdata, "eng");

        Assert.Contains(engine.Recognize(gray, width, height, Dpi, Ct).Words, static w => w.Text == "Pagewing");
    }

    [Fact]
    public void InstalledLanguagesListsTheTraineddataFiles()
    {
        using var folder = new TempFolderHere();
        foreach (var name in new[] { "eng.traineddata", "deu.traineddata", "notes.txt", "Bad Name.traineddata", "frk_old.traineddata" })
        {
            File.WriteAllBytes(Path.Combine(folder.Path, name), [1]);
        }

        Assert.Equal(["deu", "eng", "frk_old"], OcrEngine.InstalledLanguages(folder.Path));
        Assert.Empty(OcrEngine.InstalledLanguages(Path.Combine(folder.Path, "missing")));
    }

    private sealed class TempFolderHere : IDisposable
    {
        public TempFolderHere() => Path = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pagewing-ocr-" + Guid.NewGuid().ToString("N"))).FullName;

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

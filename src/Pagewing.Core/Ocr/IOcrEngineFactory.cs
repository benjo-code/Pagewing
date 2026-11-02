using Pagewing.Ocr;

namespace Pagewing.Core.Ocr;

/// <summary>One recognition engine. Not thread-safe: the runner gives each worker its own and never shares one.</summary>
public interface IOcrEngine : IDisposable
{
    /// <summary>Recognizes the words of an 8-bit gray image (<c>width * height</c> bytes, 255 is white).</summary>
    /// <param name="gray">The pixels.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="dpi">The resolution of the image.</param>
    /// <param name="timeout">The time limit for the image.</param>
    /// <param name="cancellationToken">Checked before the call; a call that has begun may run to its end.</param>
    /// <returns>The words with boxes in pixels from the top left of the image.</returns>
    /// <exception cref="OcrEngineException">The engine failed or took longer than the limit.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled before the call.</exception>
    OcrRecognition Recognize(ReadOnlySpan<byte> gray, int width, int height, int dpi, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Makes engines for a set of languages (the real one for Tesseract, a fake in tests).</summary>
public interface IOcrEngineFactory
{
    /// <summary>Makes an engine.</summary>
    /// <param name="languages">The normalized language selection (<c>deu+eng</c>).</param>
    /// <exception cref="OcrEngineException">The library or the language data is missing or damaged.</exception>
    IOcrEngine Create(string languages);
}

/// <summary>The real engines: Tesseract over the language files of one folder.</summary>
/// <param name="tessdataFolder">The folder with the <c>.traineddata</c> files of the languages (see <see cref="TessdataStore.ResolveFolder"/>).</param>
public sealed class TesseractEngineFactory(string tessdataFolder) : IOcrEngineFactory
{
    /// <summary>Gets whether the OCR library is next to the program. Looks for the file only; loads nothing.</summary>
    public static bool IsLibraryAvailable => TesseractLibrary.Default.IsAvailable;

    /// <inheritdoc />
    public IOcrEngine Create(string languages) => new TesseractEngine(OcrEngine.Create(tessdataFolder, languages));

    private sealed class TesseractEngine(OcrEngine engine) : IOcrEngine
    {
        public OcrRecognition Recognize(ReadOnlySpan<byte> gray, int width, int height, int dpi, TimeSpan timeout, CancellationToken cancellationToken) =>
            engine.Recognize(gray, width, height, dpi, cancellationToken, timeout);

        public void Dispose() => engine.Dispose();
    }
}

/// <summary>How far a run is.</summary>
/// <param name="PagesDone">The pages that are decided: recognized or skipped.</param>
/// <param name="PagesTotal">The pages the run looks at.</param>
/// <param name="Recognized">How many of the done pages were recognized.</param>
/// <param name="Skipped">How many of the done pages were skipped.</param>
public sealed record OcrProgress(int PagesDone, int PagesTotal, int Recognized, int Skipped);

/// <summary>What a run found: the pages that were recognized (by page) and the report.</summary>
/// <param name="Pages">The recognized pages, in page order, each with its words in points of the displayed page.</param>
/// <param name="Report">The report of the run.</param>
public sealed record OcrRunResult(IReadOnlyList<OcrPageResult> Pages, OcrReport Report);

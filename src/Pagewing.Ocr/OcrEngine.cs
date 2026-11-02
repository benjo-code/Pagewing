using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Pagewing.Ocr;

/// <summary>A recognized word: its text, its box in pixels from the top left of the image and the engine's confidence (0 to 100).</summary>
public readonly record struct OcrWord(string Text, int Left, int Top, int Right, int Bottom, float Confidence);

/// <summary>The words of a recognized image in reading order.</summary>
/// <param name="Words">The words; empty for a blank image.</param>
public sealed record OcrRecognition(IReadOnlyList<OcrWord> Words);

/// <summary>
/// One Tesseract engine for one or more languages (<c>eng+deu</c>). Not thread-safe: every worker has its own. It reads only the
/// language files of the folder it is given; the environment (<c>TESSDATA_PREFIX</c>) is not consulted, the engine's debug output goes
/// nowhere, and nothing is written. It makes no network access (the library is built without any).
/// </summary>
public sealed unsafe class OcrEngine : IDisposable
{
    /// <summary>The time limit for one image when the caller gives none.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(300);

    private const string Extension = ".traineddata";

    private nint api;

    private OcrEngine(nint api) => this.api = api;

    /// <summary>
    /// Creates an engine. The library is loaded here, not before.
    /// </summary>
    /// <param name="tessdataDirectory">The folder with the <c>&lt;language&gt;.traineddata</c> files.</param>
    /// <param name="languages">One or more language names (<c>[a-z_]+</c>) joined by <c>+</c>.</param>
    /// <exception cref="OcrEngineException">A name is invalid or has no file (<see cref="OcrEngineException.InstalledLanguages"/> lists those that have), the library is missing, or the data is damaged.</exception>
    public static OcrEngine Create(string tessdataDirectory, string languages)
    {
        ArgumentException.ThrowIfNullOrEmpty(tessdataDirectory);
        var names = SplitLanguages(languages);
        var installed = InstalledLanguages(tessdataDirectory);
        var missing = names.Where(name => !installed.Contains(name, StringComparer.Ordinal)).ToArray();
        if (missing.Length > 0)
        {
            throw new OcrEngineException(
                OcrEngineError.LanguageMissing,
                $"No language data for {string.Join(", ", missing)}; installed: {(installed.Count == 0 ? "none" : string.Join(", ", installed))}.",
                installed);
        }

        TesseractLibrary.Default.Load();
        var handle = TesseractNative.TessBaseAPICreate();
        if (handle == 0)
        {
            throw new OcrEngineException(OcrEngineError.Failed, "The OCR engine could not be created.");
        }

        try
        {
            Initialize(handle, tessdataDirectory, string.Join('+', names));
            TesseractNative.TessBaseAPISetPageSegMode(handle, TesseractNative.PageSegModeAuto);
            return new OcrEngine(handle);
        }
        catch
        {
            TesseractNative.TessBaseAPIEnd(handle);
            TesseractNative.TessBaseAPIDelete(handle);
            throw;
        }
    }

    /// <summary>The languages that have a file in the folder, sorted; empty when the folder does not exist. Names outside <c>[a-z_]+</c> are not languages.</summary>
    public static IReadOnlyList<string> InstalledLanguages(string tessdataDirectory)
    {
        if (string.IsNullOrEmpty(tessdataDirectory) || !Directory.Exists(tessdataDirectory))
        {
            return [];
        }

        try
        {
            return [.. Directory.EnumerateFiles(tessdataDirectory, "*" + Extension)
                .Select(static file => Path.GetFileName(file)[..^Extension.Length])
                .Where(IsLanguageName)
                .Order(StringComparer.Ordinal)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Tells whether a name can be a language: one or more of <c>a</c> to <c>z</c> and <c>_</c>.</summary>
    public static bool IsLanguageName(string name) => name.Length > 0 && name.All(static c => c is (>= 'a' and <= 'z') or '_');

    /// <summary>
    /// Recognizes the words of an 8-bit gray image (one byte per pixel, <paramref name="width"/> × <paramref name="height"/>, no padding).
    /// </summary>
    /// <param name="gray">The pixels, 255 is white.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="dpi">The resolution of the image (the engine sizes its work by it).</param>
    /// <param name="cancellationToken">Checked before the call; a call that has started runs to its end or to the time limit.</param>
    /// <param name="timeout">The time limit; <see cref="DefaultTimeout"/> when <see langword="null"/>.</param>
    /// <exception cref="ArgumentException">The buffer is not <c>width * height</c> bytes.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A size or the resolution is not positive.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled before the call.</exception>
    /// <exception cref="OcrEngineException">The page took longer than the limit (<see cref="OcrEngineError.Timeout"/>) or the engine failed.</exception>
    /// <exception cref="ObjectDisposedException">The engine is disposed.</exception>
    public OcrRecognition Recognize(ReadOnlySpan<byte> gray, int width, int height, int dpi, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(api == 0, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dpi);
        if (gray.Length != (long)width * height)
        {
            throw new ArgumentException("The image must be width * height bytes of gray.", nameof(gray));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var limit = timeout ?? DefaultTimeout;
        var deadline = (int)Math.Clamp(limit.TotalMilliseconds, 1, int.MaxValue);
        var monitor = TesseractNative.TessMonitorCreate();
        try
        {
            if (monitor != 0)
            {
                TesseractNative.TessMonitorSetDeadlineMSecs(monitor, deadline);
            }

            var clock = Stopwatch.StartNew();
            fixed (byte* pixels = gray)
            {
                TesseractNative.TessBaseAPISetImage(api, pixels, width, height, 1, width);
                TesseractNative.TessBaseAPISetSourceResolution(api, dpi);
                if (TesseractNative.TessBaseAPIRecognize(api, monitor) != 0)
                {
                    throw new OcrEngineException(OcrEngineError.Failed, "The OCR engine failed to recognize the page.");
                }
            }

            if (clock.ElapsedMilliseconds >= deadline)
            {
                throw new OcrEngineException(OcrEngineError.Timeout, $"The page took longer than {limit.TotalSeconds:0} s.");
            }

            return new OcrRecognition(ReadWords());
        }
        finally
        {
            TesseractNative.TessBaseAPIClear(api);
            if (monitor != 0)
            {
                TesseractNative.TessMonitorDelete(monitor);
            }
        }
    }

    /// <summary>Frees the engine and the language data it holds.</summary>
    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref api, 0);
        if (handle != 0)
        {
            TesseractNative.TessBaseAPIEnd(handle);
            TesseractNative.TessBaseAPIDelete(handle);
        }
    }

    private static string[] SplitLanguages(string languages)
    {
        var names = (languages ?? string.Empty).Split('+');
        if (names.Length == 0 || names.Any(static name => !IsLanguageName(name)))
        {
            throw new OcrEngineException(OcrEngineError.LanguageInvalid, "A language name has the letters a to z and _ only, several are joined with +.");
        }

        return names;
    }

    private static void Initialize(nint handle, string tessdataDirectory, string languages)
    {
        // The folder is given explicitly (the engine's TESSDATA_PREFIX is not consulted) with a trailing separator; the one variable set
        // sends the engine's debug output to the null device instead of a file or the console.
        var datapath = EngineSafePath(Path.GetFullPath(tessdataDirectory)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var nullDevice = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        var path = Marshal.StringToCoTaskMemUTF8(datapath);
        var language = Marshal.StringToCoTaskMemUTF8(languages);
        var name = Marshal.StringToCoTaskMemUTF8("debug_file");
        var value = Marshal.StringToCoTaskMemUTF8(nullDevice);
        try
        {
            var names = stackalloc byte*[1];
            var values = stackalloc byte*[1];
            names[0] = (byte*)name;
            values[0] = (byte*)value;
            var result = TesseractNative.TessBaseAPIInit4(
                handle,
                (byte*)path,
                (byte*)language,
                TesseractNative.EngineModeLstmOnly,
                null,
                0,
                names,
                values,
                1,
                setOnlyNonDebugParams: 0);
            if (result != 0)
            {
                throw new OcrEngineException(OcrEngineError.DataCorrupt, $"The OCR engine rejected the language data of {languages} (damaged or not from this version).");
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
            Marshal.FreeCoTaskMem(language);
            Marshal.FreeCoTaskMem(name);
            Marshal.FreeCoTaskMem(value);
        }
    }

    /// <summary>
    /// The engine opens its files with the C runtime's <c>fopen</c>, which reads the path in the system code page. A path with characters
    /// outside ASCII (a user named Müller) therefore fails on Windows unless the process runs with the UTF-8 code page (the manifest of
    /// the application and the command line asks for it); otherwise the short (8.3) form of the folder is used, and a folder that has none
    /// is refused with a message instead of the engine reporting "damaged data". Other systems take UTF-8 paths as they are.
    /// </summary>
    private static string EngineSafePath(string path)
    {
        if (!OperatingSystem.IsWindows() || path.All(static c => c < 0x80) || WindowsNative.GetACP() == 65001)
        {
            return path;
        }

        var shortPath = WindowsNative.ShortPathOf(path);
        if (shortPath is null || !shortPath.All(static c => c < 0x80))
        {
            throw new OcrEngineException(OcrEngineError.Failed, $"The folder {path} has characters outside ASCII and no short name; the OCR engine cannot open files there.");
        }

        return shortPath;
    }

    private List<OcrWord> ReadWords()
    {
        var words = new List<OcrWord>();
        var iterator = TesseractNative.TessBaseAPIGetIterator(api);
        if (iterator == 0)
        {
            return words;
        }

        try
        {
            var page = TesseractNative.TessResultIteratorGetPageIterator(iterator);
            do
            {
                var text = TesseractNative.TessResultIteratorGetUTF8Text(iterator, TesseractNative.LevelWord);
                if (text == 0)
                {
                    continue;
                }

                string? word;
                try
                {
                    word = Marshal.PtrToStringUTF8(text);
                }
                finally
                {
                    TesseractNative.TessDeleteText(text);
                }

                int left, top, right, bottom;
                if (string.IsNullOrWhiteSpace(word)
                    || TesseractNative.TessPageIteratorBoundingBox(page, TesseractNative.LevelWord, &left, &top, &right, &bottom) == 0)
                {
                    continue;
                }

                words.Add(new OcrWord(word.Trim(), left, top, right, bottom, TesseractNative.TessResultIteratorConfidence(iterator, TesseractNative.LevelWord)));
            }
            while (TesseractNative.TessResultIteratorNext(iterator, TesseractNative.LevelWord) != 0);
        }
        finally
        {
            TesseractNative.TessResultIteratorDelete(iterator);
        }

        return words;
    }
}

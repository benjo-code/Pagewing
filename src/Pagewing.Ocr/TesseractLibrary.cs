using System.Runtime.InteropServices;

namespace Pagewing.Ocr;

/// <summary>
/// The native OCR library (Tesseract with Leptonica, <c>tesseract.dll</c> / <c>libtesseract.so</c>). It is only ever loaded by its full path from
/// one folder (next to the application), never from the search path or the working directory, and only when the first engine is created:
/// <see cref="IsAvailable"/> looks for the file and loads nothing.
/// </summary>
public sealed class TesseractLibrary
{
    private static readonly object DefaultGate = new();
    private static TesseractLibrary defaultLibrary = new(AppContext.BaseDirectory);

    private readonly object gate = new();
    private nint handle;

    /// <summary>Creates a library in a folder. Nothing is loaded.</summary>
    public TesseractLibrary(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Folder = Path.GetFullPath(directory);
    }

    /// <summary>Gets the file name of the library on this system.</summary>
    public static string FileName => OperatingSystem.IsWindows() ? "tesseract.dll" : "libtesseract.so";

    /// <summary>Gets the library the engines use: the one in the folder of the application.</summary>
    public static TesseractLibrary Default
    {
        get
        {
            lock (DefaultGate)
            {
                return defaultLibrary;
            }
        }
    }

    /// <summary>Gets the folder the library is looked for in.</summary>
    public string Folder { get; }

    /// <summary>Gets the full path of the library file.</summary>
    public string FilePath => Path.Combine(Folder, FileName);

    /// <summary>Gets whether the file is there. Loads nothing.</summary>
    public bool IsAvailable => File.Exists(FilePath);

    /// <summary>Gets whether the library has been loaded.</summary>
    public bool IsLoaded
    {
        get
        {
            lock (gate)
            {
                return handle != 0;
            }
        }
    }

    /// <summary>Points <see cref="Default"/> at another folder (tests; the application keeps its own folder). Not allowed once the default library is loaded.</summary>
    internal static void UseDirectory(string directory)
    {
        lock (DefaultGate)
        {
            if (string.Equals(defaultLibrary.Folder, Path.GetFullPath(directory), StringComparison.Ordinal))
            {
                return;
            }

            if (defaultLibrary.IsLoaded)
            {
                throw new InvalidOperationException("The default OCR library is already loaded from another folder.");
            }

            defaultLibrary = new TesseractLibrary(directory);
        }
    }

    /// <summary>Loads the library (once) and returns its handle.</summary>
    /// <exception cref="OcrEngineException">The file is missing (<see cref="OcrEngineError.LibraryMissing"/>) or cannot be loaded (<see cref="OcrEngineError.LibraryUnusable"/>).</exception>
    public nint Load()
    {
        lock (gate)
        {
            if (handle != 0)
            {
                return handle;
            }

            if (!IsAvailable)
            {
                throw new OcrEngineException(OcrEngineError.LibraryMissing, $"The OCR library {FileName} is not in {Folder}.");
            }

            if (!NativeLibrary.TryLoad(FilePath, out var loaded))
            {
                throw new OcrEngineException(OcrEngineError.LibraryUnusable, $"The OCR library {FilePath} could not be loaded.");
            }

            handle = loaded;
            return handle;
        }
    }
}

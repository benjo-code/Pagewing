namespace Pagewing.Ocr;

/// <summary>Why the engine could not be created or could not recognize.</summary>
public enum OcrEngineError
{
    /// <summary>The native library is not next to the application.</summary>
    LibraryMissing,

    /// <summary>The native library is there but cannot be loaded or lacks a function.</summary>
    LibraryUnusable,

    /// <summary>A language name is empty or outside <c>[a-z_]+</c> (joined with <c>+</c>).</summary>
    LanguageInvalid,

    /// <summary>A language has no <c>.traineddata</c> file in the folder; <see cref="OcrEngineException.InstalledLanguages"/> names those that have.</summary>
    LanguageMissing,

    /// <summary>The engine rejected the language data (damaged or from another version).</summary>
    DataCorrupt,

    /// <summary>The page took longer than the time limit.</summary>
    Timeout,

    /// <summary>Anything else the engine refused.</summary>
    Failed,
}

/// <summary>The engine could not be created or failed on a page; <see cref="Error"/> says why. The message is English and meant for logs; the interface words its own text from <see cref="Error"/>.</summary>
public sealed class OcrEngineException : Exception
{
    /// <summary>Creates the exception.</summary>
    public OcrEngineException(OcrEngineError error, string message, IReadOnlyList<string>? installedLanguages = null)
        : base(message)
    {
        Error = error;
        InstalledLanguages = installedLanguages ?? [];
    }

    /// <summary>Gets why.</summary>
    public OcrEngineError Error { get; }

    /// <summary>Gets the languages that are installed (for <see cref="OcrEngineError.LanguageMissing"/>), sorted.</summary>
    public IReadOnlyList<string> InstalledLanguages { get; }
}

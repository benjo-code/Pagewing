using System.Globalization;
using Pagewing.Ocr;

namespace Pagewing.Core.Ocr;

/// <summary>Where a language file comes from.</summary>
public enum TessdataOrigin
{
    /// <summary>It ships with the program (next to the application).</summary>
    Shipped,

    /// <summary>The user imported it into the folder of the user.</summary>
    Imported,
}

/// <summary>A language model that is installed.</summary>
/// <param name="Name">The language name (<c>deu</c>).</param>
/// <param name="Origin">Where the file is from.</param>
/// <param name="Length">The size of the file in bytes.</param>
/// <param name="Path">The full path of the file.</param>
public sealed record TessdataLanguage(string Name, TessdataOrigin Origin, long Length, string Path);

/// <summary>Why a language file was not imported.</summary>
public enum TessdataImportError
{
    /// <summary>Imported.</summary>
    None,

    /// <summary>The file name is not <c>[a-z_]+.traineddata</c>.</summary>
    InvalidName,

    /// <summary>The path is a folder or does not exist.</summary>
    NotAFile,

    /// <summary>The file is empty.</summary>
    Empty,

    /// <summary>The file is larger than <see cref="TessdataStore.MaxFileLength"/>.</summary>
    TooLarge,

    /// <summary>A file of that language was imported before and the caller did not ask to replace it.</summary>
    AlreadyImported,

    /// <summary>The file could not be read or written.</summary>
    Failed,
}

/// <summary>The result of an import.</summary>
/// <param name="Error">Why not, or <see cref="TessdataImportError.None"/>.</param>
/// <param name="Language">The language name, for a file with a valid name.</param>
/// <param name="Path">Where the file is now, when it was imported.</param>
public sealed record TessdataImportResult(TessdataImportError Error, string? Language = null, string? Path = null);

/// <summary>Languages the run was asked for have no file; <see cref="Installed"/> lists those that have.</summary>
public sealed class TessdataMissingException : Exception
{
    /// <summary>Creates the exception.</summary>
    public TessdataMissingException(IReadOnlyList<string> missing, IReadOnlyList<string> installed)
        : base($"No language data for {string.Join(", ", missing)}; installed: {(installed.Count == 0 ? "none" : string.Join(", ", installed))}.")
    {
        Missing = missing;
        Installed = installed;
    }

    /// <summary>Gets the names that have no file.</summary>
    public IReadOnlyList<string> Missing { get; }

    /// <summary>Gets the names that have one, sorted.</summary>
    public IReadOnlyList<string> Installed { get; }
}

/// <summary>
/// The language models: the ones that ship with the program (read only, never written) and the ones the user imported into a folder of their
/// own. An imported file is chosen on purpose, so it wins over a shipped file of the same name; the shipped one stays as it was. The engine
/// reads one folder: <see cref="ResolveFolder"/> gives it, a folder of copies when the chosen languages come from both places. Imported
/// files are parsed by native code and are not trusted: a name of <c>[a-z_]+</c> only, never derived from the content, and at most
/// <see cref="MaxFileLength"/> bytes.
/// </summary>
public sealed class TessdataStore
{
    /// <summary>The largest language file that is imported.</summary>
    public const long MaxFileLength = 200L * 1024 * 1024;

    /// <summary>The longest language name.</summary>
    public const int MaxNameLength = 32;

    /// <summary>The extension of a language file.</summary>
    public const string Extension = ".traineddata";

    private const string MergedFolderName = ".merged";
    private const UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly string userDirectory;
    private readonly string? shippedDirectory;

    /// <summary>Creates a store.</summary>
    /// <param name="userDirectory">The folder of the imported files (created by the first import).</param>
    /// <param name="shippedDirectory">The folder of the files that ship with the program, or <see langword="null"/>.</param>
    public TessdataStore(string userDirectory, string? shippedDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(userDirectory);
        this.userDirectory = userDirectory;
        this.shippedDirectory = string.IsNullOrEmpty(shippedDirectory) ? null : shippedDirectory;
    }

    /// <summary>
    /// The folder of the shipped files for an application in <paramref name="baseDirectory"/>: its <c>tessdata</c> folder, or, only for the command
    /// line (it sits in <c>cli/</c> and shares the one at the root of the package), the one of the parent folder. No other program folder ever reads
    /// the folder next to it (Downloads, Programs, ...).
    /// </summary>
    public static string ShippedDirectoryFor(string baseDirectory)
    {
        var own = Path.Combine(baseDirectory, "tessdata");
        if (Directory.Exists(own))
        {
            return own;
        }

        var trimmed = Path.TrimEndingDirectorySeparator(baseDirectory);
        if (!string.Equals(Path.GetFileName(trimmed), "cli", StringComparison.OrdinalIgnoreCase))
        {
            return own;
        }

        var parent = Directory.GetParent(trimmed)?.FullName;
        return parent is not null && Directory.Exists(Path.Combine(parent, "tessdata")) ? Path.Combine(parent, "tessdata") : own;
    }

    /// <summary>The installed languages, sorted by name; for a name that has both, the imported file.</summary>
    public IReadOnlyList<TessdataLanguage> List()
    {
        var byName = new SortedDictionary<string, TessdataLanguage>(StringComparer.Ordinal);
        foreach (var language in Read(shippedDirectory, TessdataOrigin.Shipped).Concat(Read(userDirectory, TessdataOrigin.Imported)))
        {
            byName[language.Name] = language;
        }

        return [.. byName.Values];
    }

    /// <summary>Imports a language file chosen by the user: copies it (never moves it) into the folder of the user.</summary>
    /// <param name="sourcePath">The file.</param>
    /// <param name="replaceImported">Whether an earlier import of the same language may be replaced.</param>
    public TessdataImportResult Import(string sourcePath, bool replaceImported = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        var fileName = Path.GetFileName(sourcePath);
        if (!fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            return new TessdataImportResult(TessdataImportError.InvalidName);
        }

        var name = fileName[..^Extension.Length];
        if (name.Length > MaxNameLength || !OcrEngine.IsLanguageName(name))
        {
            return new TessdataImportResult(TessdataImportError.InvalidName);
        }

        try
        {
            if (Directory.Exists(sourcePath) || !File.Exists(sourcePath))
            {
                return new TessdataImportResult(TessdataImportError.NotAFile, name);
            }

            var length = new FileInfo(sourcePath).Length;
            if (length == 0)
            {
                return new TessdataImportResult(TessdataImportError.Empty, name);
            }

            if (length > MaxFileLength)
            {
                return new TessdataImportResult(TessdataImportError.TooLarge, name);
            }

            var target = Path.Combine(userDirectory, name + Extension);
            if (!replaceImported && File.Exists(target))
            {
                return new TessdataImportResult(TessdataImportError.AlreadyImported, name);
            }

            EnsureUserDirectory(userDirectory);
            var temporary = Path.Combine(userDirectory, ".pw-" + Guid.NewGuid().ToString("N")[..12] + ".tmp");
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = OwnerFile;
                }

                long copied;
                using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var destination = new FileStream(temporary, options))
                {
                    copied = Copy(source, destination);
                }

                if (copied > MaxFileLength)
                {
                    File.Delete(temporary);
                    return new TessdataImportResult(TessdataImportError.TooLarge, name);
                }

                File.Move(temporary, target, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }

            return new TessdataImportResult(TessdataImportError.None, name, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new TessdataImportResult(TessdataImportError.Failed, name);
        }
    }

    /// <summary>
    /// The folder the engine reads for the languages: the folder they all come from, or, when they come from both places, a folder of copies
    /// inside the folder of the user (kept between runs, copied again when a file changed).
    /// </summary>
    /// <param name="languages">The language names (valid).</param>
    /// <exception cref="TessdataMissingException">A language has no file.</exception>
    public string ResolveFolder(IReadOnlyList<string> languages)
    {
        ArgumentNullException.ThrowIfNull(languages);
        var installed = List();
        var chosen = new List<TessdataLanguage>();
        var missing = new List<string>();
        foreach (var name in languages)
        {
            var language = installed.FirstOrDefault(l => l.Name == name);
            if (language is null)
            {
                missing.Add(name);
            }
            else
            {
                chosen.Add(language);
            }
        }

        if (missing.Count > 0)
        {
            throw new TessdataMissingException(missing, [.. installed.Select(static l => l.Name)]);
        }

        if (chosen.All(static l => l.Origin == TessdataOrigin.Shipped))
        {
            return shippedDirectory!;
        }

        if (chosen.All(static l => l.Origin == TessdataOrigin.Imported))
        {
            return userDirectory;
        }

        return Merge(chosen);
    }

    private string Merge(List<TessdataLanguage> chosen)
    {
        var folder = Path.Combine(userDirectory, MergedFolderName);
        EnsureUserDirectory(folder);
        var wanted = chosen.Select(static l => l.Name + Extension).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in Directory.EnumerateFiles(folder, "*" + Extension).Where(file => !wanted.Contains(Path.GetFileName(file))))
        {
            File.Delete(stale);
        }

        foreach (var language in chosen)
        {
            var target = Path.Combine(folder, language.Name + Extension);
            var current = File.Exists(target) ? new FileInfo(target) : null;
            var source = new FileInfo(language.Path);
            if (current is not null && current.Length == source.Length && current.LastWriteTimeUtc >= source.LastWriteTimeUtc)
            {
                continue;
            }

            var temporary = Path.Combine(folder, ".pw-" + Guid.NewGuid().ToString("N")[..12] + ".tmp");
            try
            {
                File.Copy(language.Path, temporary, overwrite: false);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(temporary, OwnerFile);
                }

                File.Move(temporary, target, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        return folder;
    }

    private static IEnumerable<TessdataLanguage> Read(string? directory, TessdataOrigin origin)
    {
        if (directory is null || !Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*" + Extension))
        {
            var name = Path.GetFileName(file)[..^Extension.Length];
            if (name.Length <= MaxNameLength && OcrEngine.IsLanguageName(name))
            {
                yield return new TessdataLanguage(name, origin, new FileInfo(file).Length, file);
            }
        }
    }

    private static void EnsureUserDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, OwnerFolder);
        }
    }

    private static long Copy(Stream source, Stream destination)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > MaxFileLength)
            {
                return total;
            }

            destination.Write(buffer, 0, read);
        }

        return total;
    }

    /// <summary>Formats a size for a message (<c>5.6 MB</c>).</summary>
    public static string FormatLength(long bytes) =>
        bytes >= 1024 * 1024 ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MB") : string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB");
}

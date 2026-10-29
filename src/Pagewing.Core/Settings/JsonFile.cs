using System.Text;

namespace Pagewing.Core.Settings;

/// <summary>
/// The file handling shared by the small JSON stores (<see cref="SettingsStore"/>, <see cref="RecentFilesStore"/>).
/// None of the methods throws for a file system reason; a malformed path counts as one.
/// </summary>
internal static class JsonFile
{
    /// <summary>Files larger than this are not read; they count as corrupt.</summary>
    internal const long MaxFileLength = 1024 * 1024;

    /// <summary>The Unix mode of the files written here unless a caller asks for another: read and write for the owner.</summary>
    internal const UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>The Unix mode of a folder made here: the owner only.</summary>
    internal const UnixFileMode OwnerFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// Reads the file. Returns <c>null</c> when it is missing, empty or unreadable; a file over
    /// <paramref name="maxLength"/> (<see cref="MaxFileLength"/> unless given) is copied to <c>&lt;path&gt;.bad</c> first
    /// (it counts as corrupt).
    /// </summary>
    internal static string? ReadText(string path, long maxLength = MaxFileLength)
    {
        string text;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            if (info.Length > maxLength)
            {
                KeepBackup(path);
                return null;
            }

            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// Writes the text into <c>&lt;path&gt;.tmp</c> in the target folder first, then replaces the file in one step.
    /// Returns <c>false</c> if that fails for a file system reason; the old file stays as it was. The target folder is made
    /// first unless <paramref name="createDirectory"/> is <see langword="false"/>, which fails for a missing folder. On Unix
    /// the temporary file (and with it the result) is created with <paramref name="unixMode"/>, by default for the owner
    /// only (<see cref="OwnerFile"/>), and a folder made here for the owner only (<see cref="OwnerFolder"/>): the files list
    /// what the user opened and what an update brings back, which no other user of the machine needs to read. Windows
    /// inherits the rules of the profile folder.
    /// </summary>
    internal static bool TryWrite(string path, string json, bool createDirectory = true, UnixFileMode? unixMode = null)
    {
        var temp = path + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (createDirectory && !string.IsNullOrEmpty(directory))
            {
                CreateFolder(directory);
            }

            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = unixMode ?? OwnerFile;
            }

            using (var stream = new FileStream(temp, options))
            {
                stream.Write(new UTF8Encoding(false).GetBytes(json));
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            DeleteQuietly(temp);
            return false;
        }
    }

    // A folder that exists keeps its mode; one that is made here is the owner's alone on Unix.
    private static void CreateFolder(string directory)
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

    /// <summary>Copies the file to <c>&lt;path&gt;.bad</c>, replacing an older copy. A failure is ignored.</summary>
    internal static void KeepBackup(string path)
    {
        try
        {
            File.Copy(path, path + ".bad", overwrite: true);
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            // The backup is a courtesy; losing it must not change the outcome.
        }
    }

    // Besides real I/O failures, a malformed path (illegal characters, unsupported format) must not throw either.
    private static bool IsFileError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException;

    /// <summary>Deletes the file if it exists. A failure is ignored.</summary>
    internal static void DeleteQuietly(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            // Nothing more to do.
        }
    }
}

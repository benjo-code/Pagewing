using System.Text;
using System.Text.Json;

namespace Pagewing.Core.Settings;

/// <summary>
/// Reads and writes the settings file. Neither method throws: a failure to read gives the defaults, a failure to
/// write gives <c>false</c>, so the app never ends or blocks over its settings.
/// </summary>
public sealed class SettingsStore(string path)
{
    /// <summary>Files larger than this are not read; they count as corrupt.</summary>
    internal const long MaxFileLength = 1024 * 1024;

    /// <summary>The settings file.</summary>
    public string FilePath { get; } = path;

    /// <summary>
    /// Loads the settings. A missing, empty or unreadable file gives <see cref="AppSettings.Default"/>; a corrupt
    /// file does too, after it has been copied once to <c>&lt;path&gt;.bad</c>. The result is always normalized.
    /// </summary>
    public AppSettings Load()
    {
        string text;
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists)
            {
                return AppSettings.Default;
            }

            if (info.Length > MaxFileLength)
            {
                KeepBackup();
                return AppSettings.Default;
            }

            text = File.ReadAllText(FilePath);
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            return AppSettings.Default;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return AppSettings.Default;
        }

        try
        {
            var settings = JsonSerializer.Deserialize(text, SettingsJsonContext.Default.AppSettings);
            if (settings is not null)
            {
                return settings.Normalized();
            }
        }
        catch (JsonException)
        {
            // Falls through to the backup below.
        }

        KeepBackup();
        return AppSettings.Default;
    }

    /// <summary>
    /// Writes the settings: into <c>&lt;path&gt;.tmp</c> in the target folder first, then it replaces the file in one
    /// step. Returns <c>false</c> if that fails for a file system reason; the old file stays as it was.
    /// </summary>
    public bool TrySave(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var temp = FilePath + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(FilePath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(new UTF8Encoding(false).GetBytes(json));
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            DeleteQuietly(temp);
            return false;
        }
    }

    // Besides real I/O failures, a malformed path (illegal characters, unsupported format) must not throw either.
    private static bool IsFileError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException;

    private void KeepBackup()
    {
        try
        {
            File.Copy(FilePath, FilePath + ".bad", overwrite: true);
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            // The backup is a courtesy; losing it must not change the outcome.
        }
    }

    private static void DeleteQuietly(string file)
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

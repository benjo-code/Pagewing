namespace Pagewing.Core.Saving;

/// <summary>
/// The length and last write time of a file, taken when a document is opened, to notice before saving that another
/// program has changed or replaced the file in the meantime.
/// </summary>
/// <param name="Length">The length in bytes.</param>
/// <param name="LastWriteUtc">The last write time in UTC.</param>
public readonly record struct FileStamp(long Length, DateTime LastWriteUtc)
{
    /// <summary>Reads the stamp of a file; <see langword="null"/> when it does not exist, is a folder or cannot be read.</summary>
    /// <param name="path">The path of the file.</param>
    public static FileStamp? Of(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp(info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}

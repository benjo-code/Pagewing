using Pagewing.Core.Updates;

namespace Pagewing.App.Services;

/// <summary>What the installers of the system (<see cref="IUpdateApplier"/>) and the Windows helper have in common.</summary>
internal static class UpdateApplierSupport
{
    /// <summary>Manifest, signature and session are small; anything larger is not one of ours.</summary>
    public const long MaxSmallFile = 1024 * 1024;

    /// <summary>
    /// The windows as they are at this moment (call on the UI thread); an empty session when they cannot be read, which
    /// restores nothing.
    /// </summary>
    public static SessionSnapshot Capture(Func<SessionSnapshot> capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        try
        {
            return capture();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return SessionSnapshot.Empty;
        }
    }

    /// <summary>
    /// Writes <paramref name="snapshot"/> as <c>session.json</c> in <paramref name="configDirectory"/>: a file of an earlier
    /// attempt is deleted first and never comes back, and the result is <see langword="true"/> (pass
    /// <c>--restore-session</c>) only when something was written now.
    /// </summary>
    public static bool WriteSession(string configDirectory, SessionSnapshot snapshot)
    {
        var store = new SessionStore(Path.Combine(configDirectory, SessionStore.FileName));
        store.Delete();
        return snapshot.Windows.Count > 0 && store.TrySave(snapshot);
    }

    /// <summary>Deletes the session file again (the new version did not start; the file must not outlive the attempt).</summary>
    public static void DeleteSession(string configDirectory) =>
        new SessionStore(Path.Combine(configDirectory, SessionStore.FileName)).Delete();

    /// <summary>
    /// Gets <paramref name="staging"/> as a full path when it is a folder directly below <c>&lt;config&gt;/updates</c> named
    /// by a version (no link), else <see langword="null"/>.
    /// </summary>
    public static string? AcceptedStaging(string staging, string configDirectory)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(staging));
            var updates = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(configDirectory, UpdateStaging.DirectoryName)));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(Path.GetDirectoryName(full), updates, comparison) || !UpdateVersion.TryParse(Path.GetFileName(full), out _))
            {
                return null;
            }

            var folder = new DirectoryInfo(full);
            return folder.Exists && !folder.Attributes.HasFlag(FileAttributes.ReparsePoint)
                && !new DirectoryInfo(updates).Attributes.HasFlag(FileAttributes.ReparsePoint)
                ? full
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Reads a small file (at most <see cref="MaxSmallFile"/>); <see langword="null"/> when it is missing, unreadable or larger.</summary>
    public static byte[]? ReadSmall(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= MaxSmallFile ? File.ReadAllBytes(path) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

using Pagewing.Core.Saving;

namespace Pagewing.Cli.Commands;

/// <summary>
/// The rules every command that writes a file shares: the output is never the input, its folder exists, and an existing
/// output is only replaced with <c>--force</c> (milestone 13, spec 5.6). The write itself goes through the checked, atomic
/// save of the core.
/// </summary>
internal static class OutputGuard
{
    private const int MaxResolvePasses = 8;

    /// <summary>Checks <paramref name="output"/> before anything is opened or written.</summary>
    /// <param name="input">The input file.</param>
    /// <param name="output">The file to write.</param>
    /// <param name="force">Whether <c>--force</c> was given: an existing output may be replaced (the input never).</param>
    /// <remarks>
    /// "Same file" is decided by the identity the file system reports (<see cref="FileIdentity"/>), so a directory junction,
    /// a symbolic link in any part of the path, a <c>subst</c> drive, an 8.3 name or a hard link to the input is refused,
    /// with or without <paramref name="force"/> (a hard link is refused although replacing it would only replace that name:
    /// stricter than needed, on purpose). Before that, a cheap comparison of the paths with every existing folder and file
    /// link resolved; it also catches a name that does not exist yet but lies in a linked folder of the input. When the
    /// system cannot report an identity, only that comparison of paths protects the input.
    /// </remarks>
    /// <exception cref="CliException">The output is the input, a directory, in a folder that does not exist, or an existing file and <paramref name="force"/> is <see langword="false"/>.</exception>
    public static void Check(string input, string output, bool force)
    {
        ArgumentException.ThrowIfNullOrEmpty(input);
        ArgumentException.ThrowIfNullOrEmpty(output);
        string inputFull;
        string outputFull;
        try
        {
            inputFull = Path.GetFullPath(input);
            outputFull = Path.GetFullPath(output);
            if (SamePath(Canonical(inputFull), Canonical(outputFull)))
            {
                throw new CliException("The output file must not be the input file.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CliException($"Invalid output path '{output}': {ex.Message}", ex);
        }

        if (Directory.Exists(outputFull))
        {
            throw new CliException($"'{output}' is a directory; give the name of the file to write.");
        }

        var folder = Path.GetDirectoryName(outputFull);
        if (folder is null || !Directory.Exists(folder))
        {
            throw new CliException($"The folder of the output file does not exist: '{folder ?? output}'.");
        }

        if (!File.Exists(outputFull))
        {
            return;
        }

        if (FileIdentity.AreSameFile(inputFull, outputFull) == true)
        {
            throw new CliException("The output file must not be the input file (it is the same file under another name or path).");
        }

        if (!force)
        {
            throw new CliException($"'{output}' already exists. Use --force to replace it.");
        }
    }

    /// <summary>Checks the folder a command that writes several files into a folder (<c>split</c>) was given, before anything is opened.</summary>
    /// <param name="folder">The folder as the user wrote it.</param>
    /// <exception cref="CliException">The folder does not exist (it is never created: the user names the place the files go to) or is a file.</exception>
    public static void CheckFolder(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        try
        {
            if (!Directory.Exists(Path.GetFullPath(folder)))
            {
                throw new CliException($"The output folder does not exist: '{folder}'.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CliException($"Invalid output folder '{folder}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The full path with every link in it resolved: a junction or symbolic link in any folder of the path and the file itself.
    /// Components that do not exist stay as they are.
    /// </summary>
    private static string Canonical(string fullPath)
    {
        var current = fullPath;
        for (var pass = 0; pass < MaxResolvePasses; pass++)
        {
            var resolved = ResolveOnce(current);
            if (SamePath(resolved, current))
            {
                return resolved;
            }

            current = resolved;
        }

        return current;
    }

    private static string ResolveOnce(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var current = root;
        foreach (var part in fullPath[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                var info = Directory.Exists(current) ? (FileSystemInfo)new DirectoryInfo(current) : new FileInfo(current);
                if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    current = target.FullName;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A part that cannot be asked stays as it is; the identity check still protects an existing output.
            }
        }

        return current;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

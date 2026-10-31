namespace Pagewing.Cli.Commands;

/// <summary>
/// The rules every command that writes a file shares: the output is never the input, and an existing output is only
/// replaced with <c>--force</c> (milestone 13, spec 5.6). The write itself goes through the checked, atomic save of the core.
/// </summary>
internal static class OutputGuard
{
    /// <summary>Checks <paramref name="output"/> before anything is opened or written.</summary>
    /// <param name="input">The input file.</param>
    /// <param name="output">The file to write.</param>
    /// <param name="force">Whether <c>--force</c> was given: an existing output may be replaced (the input never).</param>
    /// <exception cref="CliException">The output is the input (also through a link or another spelling of the path, with or without <paramref name="force"/>), a directory, or an existing file and <paramref name="force"/> is <see langword="false"/>.</exception>
    public static void Check(string input, string output, bool force)
    {
        ArgumentException.ThrowIfNullOrEmpty(input);
        ArgumentException.ThrowIfNullOrEmpty(output);
        string outputFull;
        try
        {
            outputFull = Path.GetFullPath(output);
            if (SamePath(Canonical(Path.GetFullPath(input)), Canonical(outputFull)))
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

        if (File.Exists(outputFull) && !force)
        {
            throw new CliException($"'{output}' already exists. Use --force to replace it.");
        }
    }

    /// <summary>
    /// The path with symbolic links resolved when the file exists, so a link to the input is the input. A hard link is
    /// another name for the same bytes and is not recognised; replacing it replaces only that name, never the input's.
    /// </summary>
    private static string Canonical(string fullPath)
    {
        try
        {
            return new FileInfo(fullPath).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? fullPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return fullPath;
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

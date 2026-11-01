using System.Globalization;
using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;

namespace Pagewing.Cli.Commands;

/// <summary>
/// <c>pagewing split</c>: cuts a document into several new files (every <i>n</i> pages, by ranges, by top-level bookmarks). Every
/// file is a page plan of the input written by the checked, atomic full rewrite; what a file loses (its bookmarks to the other
/// pages, page labels, a digital signature which is then shown as invalid) is printed on stderr as <c>warning: &lt;file&gt;: …</c>
/// lines before the first byte is written. Nothing is replaced without <c>--force</c> (all files in the way are named before
/// anything is written), the input never is, and the files are always directly inside the folder (the names made of bookmark
/// titles are made safe by the core). The path of each written file is printed on stdout, one per line, also of the files that
/// were finished when a later one failed or the command was canceled.
/// </summary>
internal static class SplitCommand
{
    public static async Task RunAsync(
        PdfDocument document,
        CliOptions options,
        string? password,
        TextWriter output,
        TextWriter error,
        CancellationToken ct,
        IProgress<double>? progress = null)
    {
        var inputPath = Path.GetFullPath(options.File);
        var directory = options.OutputDirectory;
        var parts = await PlanAsync(document, options, ct).ConfigureAwait(false);
        var targets = DocumentSplitter.TargetPaths(parts, directory);
        RefuseFilesInTheWay(targets, inputPath, options.Force);

        IReadOnlyList<PageEditReport> reports;
        try
        {
            reports = await DocumentSplitter.AnalyzeAsync(document, parts, ct).ConfigureAwait(false);
        }
        catch (PageEditException ex)
        {
            throw new CliException(ex.Message, ex);
        }

        for (var i = 0; i < reports.Count; i++)
        {
            if (reports[i].IsRefused)
            {
                throw new CliException($"'{Path.GetFileName(targets[i])}' cannot be written: {PageLossText.RefusalOf(reports[i])}");
            }
        }

        // The user is told what is lost before the first byte is written, file by file.
        for (var i = 0; i < reports.Count; i++)
        {
            PageLossText.WriteWarnings(reports[i], error, prefix: $"{Path.GetFileName(targets[i])}: ");
        }

        await error.FlushAsync(ct).ConfigureAwait(false);

        var written = new List<string>();
        try
        {
            var file = PageSourceFile.Of(inputPath, password, document);
            await DocumentSplitter.WriteAsync(new PdfEngine(), document, file, parts, directory, options.Force, progress, ct, written).ConfigureAwait(false);
        }
        catch (DocumentSplitException ex)
        {
            throw new CliException(Describe(ex, written.Count, parts.Count), ex);
        }
        catch (OperationCanceledException)
        {
            if (written.Count > 0)
            {
                await error.WriteLineAsync($"warning: {Written(written.Count, parts.Count)} before the command was canceled; they were kept.").ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            // What exists is on stdout, whether or not the last file was written.
            foreach (var path in written)
            {
                await output.WriteLineAsync(Path.Combine(directory, Path.GetFileName(path))).ConfigureAwait(false);
            }

            await output.FlushAsync().ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<SplitPart>> PlanAsync(PdfDocument document, CliOptions options, CancellationToken ct)
    {
        var baseName = Path.GetFileNameWithoutExtension(options.File);
        try
        {
            var mode = options.Ranges is not null
                ? SplitMode.Ranges(options.Ranges)
                : options.SplitBookmarks
                    ? SplitMode.Bookmarks(await document.GetOutlineAsync(ct).ConfigureAwait(false))
                    : SplitMode.Every(options.Every);
            return DocumentSplitter.Plan(document.PageCount, mode, baseName);
        }
        catch (ArgumentOutOfRangeException ex) when (ex.ActualValue is int)
        {
            throw CliApp.PageOutOfRange(ex, document.PageCount);
        }
        catch (FormatException ex)
        {
            throw new UsageException($"Invalid value for --ranges: {ex.Message}");
        }
        catch (PageEditException ex)
        {
            throw new CliException(ex.Message, ex);
        }
        catch (DocumentSplitException ex) when (ex.Error == DocumentSplitError.NoBookmarks)
        {
            throw new CliException("The file has no top-level bookmark with a target page, so it cannot be split by bookmarks. Nothing was written.", ex);
        }
    }

    // Everything in the way is named at once, before the warnings and before the first byte.
    private static void RefuseFilesInTheWay(IReadOnlyList<string> targets, string inputPath, bool force)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var inTheWay = new List<string>();
        var folders = new List<string>();
        foreach (var target in targets)
        {
            var isFolder = Directory.Exists(target);
            var exists = isFolder || File.Exists(target);
            if (exists && (string.Equals(target, inputPath, comparison) || FileIdentity.AreSameFile(target, inputPath) == true))
            {
                throw new CliException($"A part must not replace the input file ('{Path.GetFileName(target)}'), with or without --force. Nothing was written.");
            }

            if (isFolder)
            {
                // --force replaces files, never a folder: it is not a "file that exists", and asking for --force would mislead.
                folders.Add(Path.GetFileName(target));
            }
            else if (exists && !force)
            {
                inTheWay.Add(Path.GetFileName(target));
            }
        }

        if (inTheWay.Count > 0 || folders.Count > 0)
        {
            var sentences = new List<string>();
            sentences.AddRange(folders.Select(static name => $"'{name}' is a folder and cannot be replaced."));
            if (inTheWay.Count > 0)
            {
                sentences.Add($"These files already exist: {string.Join(", ", inTheWay.Select(static name => $"'{name}'"))}. Use --force to replace them.");
            }

            throw new CliException($"{string.Join(" ", sentences)} Nothing was written.");
        }
    }

    private static string Describe(DocumentSplitException ex, int written, int total)
    {
        var name = ex.Paths.Count > 0 ? $"'{Path.GetFileName(ex.Paths[0])}'" : "a part";
        var tail = written > 0 ? $" {Written(written, total)} before this; they were kept." : " Nothing was written.";
        return ex.Error switch
        {
            DocumentSplitError.NoBookmarks => "The file has no top-level bookmark with a target page, so it cannot be split by bookmarks. Nothing was written.",
            DocumentSplitError.DirectoryMissing => "The output folder does not exist. Nothing was written.",
            DocumentSplitError.TargetExists when ex.PartIndex < 0 =>
                $"These files already exist: {string.Join(", ", ex.Paths.Select(static path => $"'{Path.GetFileName(path)}'"))}. Use --force to replace them. Nothing was written.",
            DocumentSplitError.TargetExists => $"{name} appeared while the command ran; it was not replaced. Use --force to replace existing files.{tail}",
            DocumentSplitError.TargetIsSource => "A part must not replace the input file, with or without --force. Nothing was written.",
            DocumentSplitError.PartFailed => $"Writing {name} failed: {Reason(ex.InnerException, ex.Paths.Count > 0 ? ex.Paths[0] : string.Empty)}{tail}",
            _ => $"{ex.Message}{tail}",
        };
    }

    // The cause of a failed part, in a sentence a user can act on.
    private static string Reason(Exception? inner, string target) => inner switch
    {
        SaveFailureException save => SaveFailureText.Describe(save, target, nothingWritten: false),
        PdfOpenException open => $"the input file cannot be read: {open.Message}",
        { } other => other.Message,
        _ => "the reason is unknown.",
    };

    private static string Written(int written, int total) =>
        string.Create(CultureInfo.InvariantCulture, $"{written} of {total} files were written");
}
